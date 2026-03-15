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
    ServerStatus serverStatus) : BackgroundService
{
    private static readonly TimeSpan SleepDuration = TimeSpan.FromHours(4);
    private static readonly TimeSpan ProcessingPlaceholder = TimeSpan.FromSeconds(5);

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
        logger.LogInformation("MediaSkipDetector starting");

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

                // Placeholder: sleep 5 seconds instead of actual processing
                await clock.Delay(ProcessingPlaceholder, stoppingToken);

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
}
