namespace MediaSkipDetector;

/// <summary>
/// Tracks server runtime state for the /status and /health endpoints.
/// Registered as a singleton in DI.
/// </summary>
public class ServerStatus
{
    public DateTime StartedAt { get; } = DateTime.Now;
    public string Health { get; set; } = "healthy";
}
