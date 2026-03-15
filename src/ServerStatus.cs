namespace MediaSkipDetector;

public enum ScanState
{
    Idle,
    Scanning,
    Processing,
    Sleeping
}

/// <summary>
/// Tracks server runtime state for the /status and /health endpoints.
/// Registered as a singleton in DI.
/// </summary>
public class ServerStatus
{
    public DateTime StartedAt { get; } = DateTime.Now;
    public string Health { get; set; } = "healthy";

    public ScanState ScanState { get; set; } = ScanState.Idle;
    public DateTime? LastScanTime { get; set; }
    public DateTime? NextScanTime { get; set; }
    public ScanCandidate? CurrentItem { get; set; }
    public ScanResult? LastScanResult { get; set; }
    public WorkQueue? WorkQueue { get; set; }
    public int ProcessedInCurrentRun { get; set; }
    public int TotalInCurrentRun { get; set; }
}
