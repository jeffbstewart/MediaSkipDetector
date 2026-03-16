namespace MediaSkipDetector;

public enum ScanState
{
    Idle,
    Scanning,
    Processing,
    Sleeping
}

/// <summary>
/// One entry in the recent intro/credits detection history.
/// </summary>
public record AnalysisHistoryEntry(
    DateTime Timestamp,
    string DirectoryName,
    int EpisodeCount,
    int IntrosFound,
    int CreditsFound,
    int Comparisons,
    TimeSpan Elapsed,
    string? Error);

/// <summary>
/// Tracks server runtime state for the /status and /health endpoints.
/// Registered as a singleton in DI.
/// </summary>
public class ServerStatus
{
    private const int MaxHistoryEntries = 20;
    private readonly LinkedList<AnalysisHistoryEntry> _analysisHistory = new();
    private readonly object _historyLock = new();

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

    public void RecordAnalysis(AnalysisHistoryEntry entry)
    {
        lock (_historyLock)
        {
            _analysisHistory.AddFirst(entry);
            while (_analysisHistory.Count > MaxHistoryEntries)
                _analysisHistory.RemoveLast();
        }
    }

    /// <summary>
    /// Updates the most recent history entry with credits detection results.
    /// Called after credits analysis completes (which runs immediately after intro analysis).
    /// </summary>
    public void UpdateLatestCredits(int creditsFound)
    {
        lock (_historyLock)
        {
            if (_analysisHistory.First?.Value is { } latest)
            {
                _analysisHistory.RemoveFirst();
                _analysisHistory.AddFirst(latest with { CreditsFound = creditsFound });
            }
        }
    }

    public List<AnalysisHistoryEntry> GetAnalysisHistory()
    {
        lock (_historyLock)
            return [.. _analysisHistory];
    }
}
