namespace MediaSkipDetector;

/// <summary>
/// Mockable clock interface for testable time and delay operations.
/// Tests can inject a FakeClock that returns controlled times and completes delays immediately.
/// </summary>
public interface IClock
{
    DateTime Now { get; }
    Task Delay(TimeSpan duration, CancellationToken ct);
}

public class SystemClock : IClock
{
    public DateTime Now => DateTime.Now;
    public Task Delay(TimeSpan duration, CancellationToken ct) => Task.Delay(duration, ct);
}
