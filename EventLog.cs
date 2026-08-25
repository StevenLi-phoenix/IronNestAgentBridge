namespace IronNestAgentBridge;

/// <summary>
/// Thread-safe ring buffer of bridge events with monotonically increasing sequence ids.
/// HTTP long-polling reads from any thread; game-side producers append from the main thread.
/// </summary>
public static class EventLog
{
    private const int Capacity = 2048;
    private static readonly object Gate = new();
    private static readonly List<BridgeEvent> Events = new();
    private static long _nextSeq = 1;

    public static void Append(string type, string source, string text, object? data = null)
    {
        lock (Gate)
        {
            Events.Add(new BridgeEvent
            {
                Seq = _nextSeq++,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = type,
                Source = source,
                Text = text,
                Data = data,
            });
            if (Events.Count > Capacity)
                Events.RemoveRange(0, Events.Count - Capacity);
            Monitor.PulseAll(Gate);
        }
    }

    /// <summary>Blocking long-poll: returns events with Seq &gt; since, waiting up to timeoutMs.</summary>
    public static List<BridgeEvent> WaitForEvents(long since, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        lock (Gate)
        {
            while (true)
            {
                var result = Events.Where(e => e.Seq > since).ToList();
                if (result.Count > 0)
                    return result;
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                    return new List<BridgeEvent>();
                Monitor.Wait(Gate, (int)Math.Min(remaining, 1000));
            }
        }
    }

    public static long LatestSeq
    {
        get { lock (Gate) return _nextSeq - 1; }
    }

    /// <summary>
    /// Full-reset support: drop all buffered events (sequence stays monotonic). Telegraph
    /// and map pollers regenerate current-state events after rebinding, so a restarted
    /// agent rebuilds awareness from live reality instead of replaying stale history.
    /// </summary>
    public static void Clear()
    {
        lock (Gate)
        {
            Events.Clear();
            Monitor.PulseAll(Gate);
        }
    }
}
