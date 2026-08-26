namespace IronNestAgentBridge;

/// <summary>
/// Process-global ring buffer of battlefield events with long-poll support. Both the HTTP
/// <c>GET /events</c> endpoint and the in-process agent thread consume the same log in parallel:
/// reads are broadcast, they never consume and never lock each other out.
///
/// Producers all run on the main thread, but the log itself tolerates any thread.
/// </summary>
public static class EventLog
{
    /// <summary>Hard memory bound. On overflow the oldest entries are dropped.</summary>
    public const int Capacity = 2048;

    private static readonly object Lock = new();
    private static readonly Queue<BridgeEvent> Buffer = new();

    /// <summary>Sequence numbers start at 1, increase monotonically and are never reused.</summary>
    private static long _nextSeq = 1;

    /// <summary>
    /// Mission clock snapshot stamped onto every appended event, written by the mod's update
    /// loop. Two formats, both without a date part:
    /// <list type="bullet">
    /// <item>"HH:mm" while the in-game 24h world clock is available (the authoritative axis,
    /// shared with teleprinter timestamps, snapshot gameTime and tool receipts);</item>
    /// <item>"mm:ss" when only the mission stopwatch exists (fallback).</item>
    /// </list>
    /// Empty string before any clock is running — every consumer must tolerate that.
    /// </summary>
    public static volatile string GameClock = "";

    /// <summary>Newest sequence number in the log; 0 when nothing has ever been appended.</summary>
    public static long LatestSeq
    {
        get { lock (Lock) return _nextSeq - 1; }
    }

    /// <summary>
    /// Sequence number of the oldest event still buffered. When the buffer is empty this is the
    /// sequence the next event will get, so a client whose cursor sits below it can still tell
    /// that the span in between was lost to <see cref="Clear"/> or to the capacity bound.
    /// </summary>
    public static long OldestSeq
    {
        get
        {
            lock (Lock)
            {
                return Buffer.Count == 0 ? _nextSeq : Buffer.Peek().Seq;
            }
        }
    }

    /// <summary>
    /// Appends one event. Sequence allocation, enqueue, trim and waiter wake-up all happen
    /// under a single lock so that no reader can observe a gap or a duplicate sequence.
    /// The log performs no de-duplication and no debouncing — that is the consumer's job.
    /// </summary>
    public static void Append(string type, string source, string text, object? data = null)
    {
        lock (Lock)
        {
            var ev = new BridgeEvent
            {
                Seq = _nextSeq++,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Type = type,
                Source = source,
                Text = text,
                GameTime = GameClock,
                Data = data,
            };

            Buffer.Enqueue(ev);
            while (Buffer.Count > Capacity) Buffer.Dequeue();

            Monitor.PulseAll(Lock);
        }
    }

    /// <summary>
    /// Returns every buffered event with <c>Seq &gt; since</c> in insertion order. Returns
    /// immediately when anything is already pending; otherwise blocks until an append or a
    /// <see cref="Clear"/> wakes it, or until the deadline passes — in which case it returns an
    /// empty list, never null and never an error.
    ///
    /// <paramref name="timeoutMs"/> == 0 degenerates into a non-blocking drain, which the
    /// agent's "events picked up alongside a tool call" path depends on.
    /// </summary>
    public static List<BridgeEvent> WaitForEvents(long since, int timeoutMs)
    {
        // Monotonic deadline: wall-clock adjustments must not extend or cut short a poll.
        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);

        lock (Lock)
        {
            while (true)
            {
                var hits = Collect(since);
                if (hits.Count > 0) return hits;

                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0) return hits;

                // Slice the wait so a missed pulse costs at most one second and so shutdown /
                // Clear take effect promptly.
                Monitor.Wait(Lock, (int)Math.Min(1000, remaining));
            }
        }
    }

    /// <summary>
    /// Empties the buffer and wakes every waiter. Sequence numbers keep going up, so an old
    /// client cursor can never travel back in time.
    ///
    /// Used by the full reset (F9 / new mission): stale events must never be replayed into the
    /// context of a restarted agent. The teleprinter and map pollers re-emit current state once
    /// the scene rebinds.
    /// </summary>
    public static void Clear()
    {
        lock (Lock)
        {
            Buffer.Clear();
            Monitor.PulseAll(Lock);
        }
    }

    /// <summary>Caller must hold <see cref="Lock"/>.</summary>
    private static List<BridgeEvent> Collect(long since)
    {
        var hits = new List<BridgeEvent>();
        foreach (var ev in Buffer)
        {
            if (ev.Seq > since) hits.Add(ev);
        }
        return hits;
    }
}
