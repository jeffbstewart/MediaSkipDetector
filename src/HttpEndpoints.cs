using System.Web;
using Prometheus;

namespace MediaSkipDetector;

/// <summary>
/// Registers HTTP endpoints for health monitoring, metrics, status, scan control, and shutdown.
/// </summary>
public static class HttpEndpoints
{
    public static void MapEndpoints(this WebApplication app)
    {
        app.UseHttpMetrics();
        app.MapGet("/", () => Results.Redirect("/status"));
        app.MapGet("/health", HandleHealth);
        app.MapGet("/status", HandleStatus);
        app.MapPost("/scannow", HandleScanNow);
        app.MapMetrics(); // /metrics — Prometheus
        app.MapPost("/quitquitquit", HandleShutdown);
    }

    private static IResult HandleHealth()
    {
        return Results.Ok(new { status = "healthy" });
    }

    private static IResult HandleScanNow(Worker worker)
    {
        var result = worker.RequestScanNow();
        var status = result switch
        {
            ScanNowResult.ScanRequested => "scan requested",
            ScanNowResult.RescanQueued => "rescan queued",
            ScanNowResult.RescanAlreadyQueued => "rescan already queued",
            _ => "unknown"
        };
        return Results.Ok(new { status });
    }

    private static IResult HandleStatus(
        ServerStatus status, IFpcalcService fpcalcService, IFingerprintCache fingerprintCache)
    {
        var uptime = DateTime.Now - status.StartedAt;
        var pendingItems = status.WorkQueue?.PendingItems ?? [];
        var scan = status.LastScanResult;
        var enc = (string? s) => HttpUtility.HtmlEncode(s ?? "");

        var pendingRows = "";
        foreach (var item in pendingItems)
        {
            pendingRows += $"""
                <tr>
                    <td>{enc(item.Directory.FullName)}</td>
                    <td>{item.MkvFileNames.Count}</td>
                    <td>{item.NewestMkvTimestamp:yyyy-MM-dd HH:mm}</td>
                </tr>
                """;
        }

        var currentlyProcessing = status.CurrentItem is not null
            ? $"{enc(status.CurrentItem.Directory.FullName)} [{status.ProcessedInCurrentRun}/{status.TotalInCurrentRun}]"
            : "none";

        var lastScanSection = scan is not null
            ? $"<p>Found: {scan.TotalDirectories} directories | Up to date: {scan.UpToDate} | Pending: {scan.Pending.Count}</p>"
            : "<p>No scan completed yet</p>";

        var nextScan = status.NextScanTime.HasValue
            ? $"{status.NextScanTime.Value:yyyy-MM-dd HH:mm:ss}"
            : "n/a";

        var lastScan = status.LastScanTime.HasValue
            ? $"{status.LastScanTime.Value:yyyy-MM-dd HH:mm:ss}"
            : "never";

        var fpcalcStatus = fpcalcService.IsAvailable
            ? "<span style=\"color:green;\">available</span>"
            : "<span style=\"color:red;\">NOT AVAILABLE</span>";

        var ffmpegStatus = fpcalcService.IsFfmpegAvailable
            ? "<span style=\"color:green;\">available</span>"
            : "<span style=\"color:orange;\">NOT AVAILABLE (credits detection disabled)</span>";

        var cacheCount = fingerprintCache.Count;
        var fingerprinted = ScanMetrics.FilesFingerprinted.Value;
        var fpErrors = ScanMetrics.FingerprintErrors.Value;
        var cacheHits = ScanMetrics.FingerprintCacheHits.Value;
        var cacheMisses = ScanMetrics.FingerprintCacheMisses.Value;
        var creditsFingerprinted = ScanMetrics.CreditsFingerprinted.Value;
        var creditsFpErrors = ScanMetrics.CreditsFingerprintErrors.Value;

        var html = $"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8"><title>MediaSkipDetector</title></head>
            <body style="font-family:sans-serif;margin:2em;">
            <h1>MediaSkipDetector</h1>
            <p>Running since {status.StartedAt:yyyy-MM-dd HH:mm:ss} ({uptime.Days}d {uptime.Hours}h {uptime.Minutes}m) | Health: <strong>{enc(status.Health)}</strong></p>
            <h2>Scanner</h2>
            <p>State: <strong>{status.ScanState}</strong> | Last scan: {lastScan} | Next scan: {nextScan}</p>
            <p>Currently processing: {currentlyProcessing}</p>
            <h2>Last Scan Results</h2>
            {lastScanSection}
            <h2>Fingerprinting</h2>
            <p>fpcalc: {fpcalcStatus} | ffmpeg: {ffmpegStatus} | Cache entries: {cacheCount}</p>
            <p>Intros: {fingerprinted} fingerprinted, {fpErrors} errors | Cache: {cacheHits} hits, {cacheMisses} misses</p>
            <p>Credits: {creditsFingerprinted} fingerprinted, {creditsFpErrors} errors</p>
            <h2>Detection</h2>
            <p>Bundles analyzed: {ScanMetrics.BundlesAnalyzed.Value} | Intros detected: {ScanMetrics.IntrosDetected.Value} | Credits detected: {ScanMetrics.CreditsDetected.Value} | Errors: {ScanMetrics.AnalysisErrors.Value}</p>
            {RenderAnalysisHistory(status, enc)}
            <h2>Pending Work</h2>
            <table border="1" cellpadding="6" cellspacing="0">
            <tr><th>Directory</th><th>Files</th><th>Newest MKV</th></tr>
            {pendingRows}
            </table>
            </body></html>
            """;
        return Results.Content(html, "text/html");
    }

    private static string RenderAnalysisHistory(ServerStatus status, Func<string?, string> enc)
    {
        var history = status.GetAnalysisHistory();
        if (history.Count == 0)
            return "<p><em>No analyses completed yet.</em></p>";

        var rows = "";
        foreach (var entry in history)
        {
            string outcome;
            string style;
            if (entry.Error != null)
            {
                outcome = $"Error: {enc(entry.Error)}";
                style = "color:red;";
            }
            else if (entry.IntrosFound > 0 || entry.CreditsFound > 0)
            {
                var parts = new List<string>();
                if (entry.IntrosFound > 0)
                    parts.Add($"intros in {entry.IntrosFound}/{entry.EpisodeCount}");
                if (entry.CreditsFound > 0)
                    parts.Add($"credits in {entry.CreditsFound}/{entry.EpisodeCount}");
                outcome = $"Found {string.Join(", ", parts)}";
                style = "color:green;";
            }
            else
            {
                outcome = "No intros or credits found";
                style = "color:gray;";
            }

            rows += $"""
                <tr>
                    <td>{entry.Timestamp:HH:mm:ss}</td>
                    <td>{enc(entry.DirectoryName)}</td>
                    <td>{entry.EpisodeCount}</td>
                    <td>{entry.Comparisons}</td>
                    <td style="{style}">{outcome}</td>
                    <td>{entry.Elapsed.TotalSeconds:F1}s</td>
                </tr>
                """;
        }

        return $"""
            <table border="1" cellpadding="6" cellspacing="0">
            <tr><th>Time</th><th>Directory</th><th>Episodes</th><th>Comparisons</th><th>Outcome</th><th>Elapsed</th></tr>
            {rows}
            </table>
            """;
    }

    private static IResult HandleShutdown(IHostApplicationLifetime lifetime, ILogger<ServerStatus> logger)
    {
        logger.LogInformation("Shutdown requested via /quitquitquit");
        lifetime.StopApplication();
        return Results.Ok(new { status = "shutting down" });
    }
}
