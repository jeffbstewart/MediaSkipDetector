namespace MediaSkipDetector;

public class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("MediaSkipDetector starting");

        while (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Hello, world! Scanner loop would run here.");
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }

        logger.LogInformation("MediaSkipDetector shutting down");
    }
}
