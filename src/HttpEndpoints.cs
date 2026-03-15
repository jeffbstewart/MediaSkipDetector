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

        var cacheCount = fingerprintCache.Count;
        var fingerprinted = ScanMetrics.FilesFingerprinted.Value;
        var fpErrors = ScanMetrics.FingerprintErrors.Value;
        var cacheHits = ScanMetrics.FingerprintCacheHits.Value;
        var cacheMisses = ScanMetrics.FingerprintCacheMisses.Value;

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
            <p>fpcalc: {fpcalcStatus} | Cache entries: {cacheCount}</p>
            <p>This run: {fingerprinted} fingerprinted, {fpErrors} errors | Cache: {cacheHits} hits, {cacheMisses} misses</p>
            <h2>Pending Work</h2>
            <table border="1" cellpadding="6" cellspacing="0">
            <tr><th>Directory</th><th>Files</th><th>Newest MKV</th></tr>
            {pendingRows}
            </table>
            </body></html>
            """;
        return Results.Content(html, "text/html");
    }

    private static IResult HandleShutdown(IHostApplicationLifetime lifetime, ILogger<ServerStatus> logger)
    {
        logger.LogInformation("Shutdown requested via /quitquitquit");
        lifetime.StopApplication();
        return Results.Ok(new { status = "shutting down" });
    }
}
