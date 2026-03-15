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

    public static readonly Counter FingerprintCacheHits =
        Metrics.CreateCounter("skipdetect_fingerprint_cache_hits_total", "Fingerprint cache hits during bundle preparation");

    public static readonly Counter FingerprintCacheMisses =
        Metrics.CreateCounter("skipdetect_fingerprint_cache_misses_total", "Fingerprint cache misses during bundle preparation");

    public static readonly Counter BundlesCreated =
        Metrics.CreateCounter("skipdetect_bundles_created_total", "Fingerprint bundles created since startup");

    public static readonly Counter BundlesReady =
        Metrics.CreateCounter("skipdetect_bundles_ready_total", "Fingerprint bundles that reached READY status since startup");

    public static readonly Counter FilesFingerprinted =
        Metrics.CreateCounter("skipdetect_files_fingerprinted_total", "Files successfully fingerprinted since startup");

    public static readonly Counter FingerprintErrors =
        Metrics.CreateCounter("skipdetect_fingerprint_errors_total", "Fingerprinting errors since startup");

    public static readonly Counter BundlesAnalyzed =
        Metrics.CreateCounter("skipdetect_bundles_analyzed_total", "Bundles analyzed for intros since startup");

    public static readonly Counter IntrosDetected =
        Metrics.CreateCounter("skipdetect_intros_detected_total", "Intro segments detected since startup");

    public static readonly Counter AnalysisErrors =
        Metrics.CreateCounter("skipdetect_analysis_errors_total", "Analysis errors since startup");
}
