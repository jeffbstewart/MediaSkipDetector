namespace MediaSkipDetector;

/// <summary>
/// Thread-safe priority queue for scan candidates.
/// Prioritizes by newest MKV timestamp (most recently modified first).
/// </summary>
public class WorkQueue
{
    private readonly object _lock = new();
    private readonly PriorityQueue<ScanCandidate, long> _queue = new();
    private readonly List<ScanCandidate> _orderedSnapshot = [];

    public int Count
    {
        get { lock (_lock) return _queue.Count; }
    }

    /// <summary>
    /// Returns a snapshot of pending items in priority order (for status page display).
    /// </summary>
    public List<ScanCandidate> PendingItems
    {
        get { lock (_lock) return [.. _orderedSnapshot]; }
    }

    /// <summary>
    /// Clears the queue and loads all pending candidates from a scan result.
    /// Priority: newest MKV timestamp first (negated ticks for descending order).
    /// </summary>
    public void Enqueue(ScanResult result)
    {
        lock (_lock)
        {
            _queue.Clear();
            _orderedSnapshot.Clear();

            var sorted = result.Pending
                .OrderByDescending(c => c.NewestMkvTimestamp)
                .ToList();

            foreach (var candidate in sorted)
            {
                _queue.Enqueue(candidate, -candidate.NewestMkvTimestamp.Ticks);
                _orderedSnapshot.Add(candidate);
            }
        }
    }

    public bool TryDequeue(out ScanCandidate? candidate)
    {
        lock (_lock)
        {
            if (_queue.Count == 0)
            {
                candidate = null;
                return false;
            }
            candidate = _queue.Dequeue();
            _orderedSnapshot.Remove(candidate);
            return true;
        }
    }
}
