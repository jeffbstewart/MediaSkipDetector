using Prometheus;

namespace MediaSkipDetector;

/// <summary>
/// Registers HTTP endpoints for health monitoring, metrics, status, and shutdown.
/// </summary>
public static class HttpEndpoints
{
    public static void MapEndpoints(this WebApplication app)
    {
        app.UseHttpMetrics();
        app.MapGet("/health", HandleHealth);
        app.MapGet("/status", HandleStatus);
        app.MapMetrics(); // /metrics — Prometheus
        app.MapPost("/quitquitquit", HandleShutdown);
    }

    private static IResult HandleHealth()
    {
        return Results.Ok(new { status = "healthy" });
    }

    private static IResult HandleStatus(ServerStatus status)
    {
        var uptime = DateTime.Now - status.StartedAt;
        var html = $"""
            <!DOCTYPE html>
            <html><head><title>MediaSkipDetector</title></head>
            <body style="font-family:sans-serif;margin:2em;">
            <h1>MediaSkipDetector</h1>
            <p>Running since {status.StartedAt:yyyy-MM-dd HH:mm:ss} ({uptime.Days}d {uptime.Hours}h {uptime.Minutes}m)</p>
            <p>Health: <strong>{status.Health}</strong></p>
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
