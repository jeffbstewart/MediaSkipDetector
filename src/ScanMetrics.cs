using Prometheus;

namespace MediaSkipDetector;

/// <summary>
/// Prometheus metrics for directory scanning and processing.
/// </summary>
public static class ScanMetrics
{
    public static readonly Gauge DirectoriesFound =
        Metrics.CreateGauge("skipdetect_directories_found", "Total qualifying directories found in last scan");

    public static readonly Gauge DirectoriesUpToDate =
        Metrics.CreateGauge("skipdetect_directories_up_to_date", "Directories already processed and up to date");

    public static readonly Gauge DirectoriesPending =
        Metrics.CreateGauge("skipdetect_directories_pending", "Directories pending processing");

    public static readonly Counter DirectoriesProcessed =
        Metrics.CreateCounter("skipdetect_directories_processed_total", "Total directories processed since startup");
}
