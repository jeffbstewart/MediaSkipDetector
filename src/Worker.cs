using System.Diagnostics;

namespace MediaSkipDetector;

public enum ScanNowResult
{
    ScanRequested,
    RescanQueued,
    RescanAlreadyQueued
}

public class Worker(
    ILogger<Worker> logger,
    IClock clock,
    DirectoryScanner scanner,
    WorkQueue workQueue,
    ServerStatus serverStatus,
    IFingerprintPipelineService pipeline,
    IFpcalcService fpcalc,
    IFingerprintCache fingerprintCache,
    IIntroAnalysisService introAnalysis,
    AppConfig appConfig) : BackgroundService
{
    private static readonly TimeSpan SleepDuration = TimeSpan.FromHours(4);

    private CancellationTokenSource? _sleepCts;
    private volatile bool _rescanRequested;

    public ScanNowResult RequestScanNow()
    {
        if (_sleepCts is { IsCancellationRequested: false })
        {
            _sleepCts.Cancel();
            return ScanNowResult.ScanRequested;
        }
        if (_rescanRequested)
            return ScanNowResult.RescanAlreadyQueued;
        _rescanRequested = true;
        return ScanNowResult.RescanQueued;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MediaSkipDetector starting (fpcalc: {FpcalcStatus}, ffmpeg: {FfmpegStatus})",
            fpcalc.IsAvailable ? "available" : "NOT AVAILABLE",
            fpcalc.IsFfmpegAvailable ? "available" : "NOT AVAILABLE");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Step 1: Scan directories
            serverStatus.ScanState = ScanState.Scanning;
            var scanResult = scanner.Scan();
            serverStatus.LastScanTime = clock.Now;
            serverStatus.LastScanResult = scanResult;

            // Step 2: Load into work queue
            workQueue.Enqueue(scanResult);
            serverStatus.WorkQueue = workQueue;

            // Step 3: Update metrics
            ScanMetrics.DirectoriesFound.Set(scanResult.TotalDirectories);
            ScanMetrics.DirectoriesUpToDate.Set(scanResult.UpToDate);
            ScanMetrics.DirectoriesPending.Set(scanResult.Pending.Count);

            logger.LogInformation("Queued {Count} directories for processing", scanResult.Pending.Count);

            // Step 4: Process items one at a time
            serverStatus.ScanState = ScanState.Processing;
            serverStatus.ProcessedInCurrentRun = 0;
            serverStatus.TotalInCurrentRun = scanResult.Pending.Count;
            var runStopwatch = Stopwatch.StartNew();

            while (workQueue.TryDequeue(out var candidate) && !stoppingToken.IsCancellationRequested)
            {
                serverStatus.CurrentItem = candidate;
                serverStatus.ProcessedInCurrentRun++;
                var itemNumber = serverStatus.ProcessedInCurrentRun;
                var total = serverStatus.TotalInCurrentRun;

                logger.LogInformation(
                    "Processing [{Current}/{Total}]: {Directory} ({FileCount} episodes)",
                    itemNumber, total, candidate!.Directory.FullName, candidate.MkvFileNames.Count);

                var itemStopwatch = Stopwatch.StartNew();

                // ── Intro fingerprinting (via pipeline) ──
                var prep = pipeline.PrepareBundle(candidate);
                logger.LogInformation(
                    "Bundle {Dir}: {Cached} cached, {Pending} pending, {Failed} permanently failed",
                    candidate.Directory.Name, prep.CachedCount, prep.PendingCount, prep.FailedCount);

                while (pipeline.GetNextPendingItem(prep.BundleId) is { } item
                       && !stoppingToken.IsCancellationRequested)
                {
                    FingerprintWorkItem(item, stoppingToken);
                }

                var justBecameReady = pipeline.CheckBundleCompletion(prep.BundleId);
                var alreadyReady = !justBecameReady && prep.PendingCount == 0;

                if (justBecameReady || alreadyReady)
                {
                    if (justBecameReady)
                        logger.LogInformation("Bundle READY, analyzing: {Dir}", candidate.Directory.Name);
                    else
                        logger.LogInformation("Bundle already READY (all cached), re-analyzing: {Dir}", candidate.Directory.Name);

                    AnalyzeBundle(candidate);

                    // ── Credits fingerprinting + analysis (second pass) ──
                    if (fpcalc.IsFfmpegAvailable)
                    {
                        FingerprintCredits(candidate, stoppingToken);
                        AnalyzeCredits(candidate);
                    }
                }

                itemStopwatch.Stop();
                ScanMetrics.DirectoriesProcessed.Inc();
                ScanMetrics.DirectoriesPending.Dec();

                logger.LogInformation(
                    "Completed [{Current}/{Total}]: {Directory} in {Elapsed}",
                    itemNumber, total, candidate.Directory.FullName, itemStopwatch.Elapsed);
            }

            serverStatus.CurrentItem = null;
            runStopwatch.Stop();

            if (serverStatus.ProcessedInCurrentRun > 0)
                logger.LogInformation(
                    "All {Count} directories processed in {Elapsed}",
                    serverStatus.ProcessedInCurrentRun, runStopwatch.Elapsed);

            // Step 5: Check rescan flag
            if (_rescanRequested)
            {
                _rescanRequested = false;
                logger.LogInformation("Rescan requested, starting another pass");
                continue;
            }

            // Step 6: Sleep 4 hours (cancellable via /scannow)
            serverStatus.ScanState = ScanState.Sleeping;
            serverStatus.NextScanTime = clock.Now + SleepDuration;
            logger.LogInformation("Sleeping until {NextScan} (4 hours)", serverStatus.NextScanTime);

            _sleepCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            try
            {
                await clock.Delay(SleepDuration, _sleepCts.Token);
                logger.LogInformation("Sleep complete, starting scheduled scan");
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("Woken by /scannow request");
            }
            finally
            {
                _sleepCts.Dispose();
                _sleepCts = null;
            }
        }

        serverStatus.ScanState = ScanState.Idle;
        logger.LogInformation("MediaSkipDetector shutting down");
    }

    private void AnalyzeBundle(ScanCandidate candidate)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var result = introAnalysis.AnalyzeBundle(candidate);
            sw.Stop();

            var displayName = FormatDirectoryDisplay(candidate.Directory);
            serverStatus.RecordAnalysis(new AnalysisHistoryEntry(
                clock.Now, displayName, candidate.MkvFileNames.Count,
                result.EpisodesWithIntros, result.EpisodesWithCredits,
                result.TotalComparisons, sw.Elapsed, null));

            if (result.EpisodesWithIntros > 0)
            {
                logger.LogInformation(
                    "Analysis complete for {Dir}: found intros in {Count} episodes ({Comparisons} comparisons) in {Elapsed}",
                    candidate.Directory.Name, result.EpisodesWithIntros, result.TotalComparisons,
                    sw.Elapsed);
                ScanMetrics.IntrosDetected.Inc(result.EpisodesWithIntros);
            }
            else
            {
                logger.LogInformation(
                    "Analysis complete for {Dir}: no intros found ({Comparisons} comparisons) in {Elapsed}",
                    candidate.Directory.Name, result.TotalComparisons, sw.Elapsed);
            }

            ScanMetrics.BundlesAnalyzed.Inc();
        }
        catch (Exception ex)
        {
            logger.LogWarning("Analysis failed for {Dir}: {Error}", candidate.Directory.Name, ex.Message);

            var displayName = FormatDirectoryDisplay(candidate.Directory);
            serverStatus.RecordAnalysis(new AnalysisHistoryEntry(
                clock.Now, displayName, candidate.MkvFileNames.Count,
                0, 0, 0, TimeSpan.Zero, ex.Message));

            ScanMetrics.AnalysisErrors.Inc();
        }
    }

    /// <summary>
    /// Fingerprints the tail audio of each episode for credits detection.
    /// Runs inline (not through the pipeline work queue).
    /// </summary>
    private void FingerprintCredits(ScanCandidate candidate, CancellationToken ct)
    {
        var directoryPath = candidate.Directory.FullName;
        var tailSeconds = appConfig.CreditsFingerprintSeconds;
        var cached = 0;
        var fingerprinted = 0;
        var errors = 0;

        foreach (var fileName in candidate.MkvFileNames)
        {
            if (ct.IsCancellationRequested) break;

            var fullPath = Path.Combine(directoryPath, fileName);
            var relativePath = FingerprintCache.ToRelativePath(appConfig.MediaRoot, fullPath);

            FileInfo fileInfo;
            try
            {
                fileInfo = new FileInfo(fullPath);
                if (!fileInfo.Exists) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            // Check credits cache
            var existing = fingerprintCache.Get(relativePath, fileInfo.Length, fileInfo.LastWriteTimeUtc, "CREDITS");
            if (existing != null)
            {
                cached++;
                continue;
            }

            // Cache miss — fingerprint the tail
            try
            {
                var sw = Stopwatch.StartNew();
                var result = fpcalc.RunTail(fullPath, tailSeconds, ct);
                sw.Stop();

                fingerprintCache.Put(relativePath, fileInfo.Length, fileInfo.LastWriteTimeUtc,
                    result.Fingerprint, result.DurationSeconds, "CREDITS");

                fingerprinted++;
                ScanMetrics.CreditsFingerprinted.Inc();

                logger.LogInformation("Credits fingerprinted {File}: {Duration:F1}s episode, {Points} points in {Elapsed}",
                    fileName, result.DurationSeconds, result.Fingerprint.Length, sw.Elapsed);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Let shutdown propagate
            }
            catch (Exception ex)
            {
                errors++;
                ScanMetrics.CreditsFingerprintErrors.Inc();
                logger.LogWarning("Failed to credits-fingerprint {File}: {Error}", fileName, ex.Message);
            }
        }

        logger.LogInformation("Credits fingerprinting for {Dir}: {Cached} cached, {New} new, {Errors} errors",
            candidate.Directory.Name, cached, fingerprinted, errors);
    }

    private void AnalyzeCredits(ScanCandidate candidate)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var result = introAnalysis.AnalyzeCredits(candidate);
            sw.Stop();

            var displayName = FormatDirectoryDisplay(candidate.Directory);

            if (result.EpisodesWithCredits > 0)
            {
                logger.LogInformation(
                    "Credits analysis complete for {Dir}: found credits in {Count} episodes ({Comparisons} comparisons) in {Elapsed}",
                    candidate.Directory.Name, result.EpisodesWithCredits, result.TotalComparisons,
                    sw.Elapsed);
                ScanMetrics.CreditsDetected.Inc(result.EpisodesWithCredits);

                // Update the most recent analysis history entry with credits data
                serverStatus.UpdateLatestCredits(result.EpisodesWithCredits);
            }
            else
            {
                logger.LogInformation(
                    "Credits analysis complete for {Dir}: no credits found ({Comparisons} comparisons) in {Elapsed}",
                    candidate.Directory.Name, result.TotalComparisons, sw.Elapsed);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Credits analysis failed for {Dir}: {Error}", candidate.Directory.Name, ex.Message);
            ScanMetrics.AnalysisErrors.Inc();
        }
    }

    /// <summary>
    /// Formats a directory for display as "ParentName / LeafName" (e.g., "Xena Warrior Princess / S06").
    /// </summary>
    private static string FormatDirectoryDisplay(DirectoryInfo dir)
    {
        var parent = dir.Parent?.Name;
        return parent != null ? $"{parent} / {dir.Name}" : dir.Name;
    }

    private void FingerprintWorkItem(WorkItemInfo item, CancellationToken ct)
    {
        if (!fpcalc.IsAvailable)
        {
            pipeline.FailWorkItem(item.Id, "fpcalc not available — set FPCALC_PATH");
            ScanMetrics.FingerprintErrors.Inc();
            return;
        }

        try
        {
            var sw = Stopwatch.StartNew();
            var result = fpcalc.Run(item.FullPath, ct);
            sw.Stop();

            // Store in cache
            var fileInfo = new FileInfo(item.FullPath);
            fingerprintCache.Put(item.RelativePath, fileInfo.Length, fileInfo.LastWriteTimeUtc,
                result.Fingerprint, result.DurationSeconds);

            pipeline.CompleteWorkItem(item.Id);
            ScanMetrics.FilesFingerprinted.Inc();

            logger.LogInformation("Fingerprinted {File}: {Duration:F1}s, {Points} points in {Elapsed}",
                item.FileName, result.DurationSeconds, result.Fingerprint.Length, sw.Elapsed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Let shutdown propagate
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to fingerprint {File}: {Error}", item.FileName, ex.Message);
            pipeline.FailWorkItem(item.Id, ex.Message);
            ScanMetrics.FingerprintErrors.Inc();
        }
    }
}
