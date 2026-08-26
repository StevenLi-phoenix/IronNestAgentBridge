using IronNestAgentBridge.GameState;

namespace IronNestAgentBridge;

/// <summary>
/// The mod's polling cadences, gathered in one place so the frame loop reads as a list of beats
/// rather than as a pile of "next due" floats.
///
/// Every beat runs off the single <c>Time.realtimeSinceStartup</c> sample the frame took at its
/// start; none of them ever reads the clock again. Sampling per beat would let the beats drift
/// apart within one frame and, worse, would make "the map tick and the FCS tick agree about now"
/// accidentally false.
///
/// The intervals themselves belong to the modules that own the work — the map reader knows how
/// often a map is worth re-reading — so they are referenced, never re-declared.
/// </summary>
public sealed class PollScheduler
{
    /// <summary>Map, impact, overdue write-off and the friendly-patrol gate.</summary>
    public Tick Map { get; } = new(MapReader.MapPollSeconds);

    /// <summary>Teleprinter roll. Does not require a bound map: dispatches arrive regardless.</summary>
    public Tick Telegraph { get; } = new(TeleprinterReader.TelegraphPollSeconds);

    /// <summary>FCS summary, the fired/failed reconciliation and the card receipt.</summary>
    public Tick Fcs { get; } = new(FcsPollSeconds);

    /// <summary>Cinematic, manual calibration, mission phase, counter-battery, world clock.</summary>
    public Tick Misc { get; } = new(MiscPollSeconds);

    /// <summary>
    /// Scene binding attempts. Idempotent and self-silencing: once bound, nothing asks again
    /// until an unbind.
    /// </summary>
    public Tick Bind { get; } = new(MapReader.BindRetrySeconds);

    /// <summary>
    /// Counter-battery broadcast. Driven by the relay's own state machine rather than by a plain
    /// interval — the countdown's start edge decides when the first announcement falls due.
    /// </summary>
    public Tick CounterBattery { get; } = new(CounterBatteryBroadcastSeconds);

    public const float FcsPollSeconds = 2f;
    public const float MiscPollSeconds = 0.5f;

    /// <summary>One announcement per 20 s while the countdown runs; quiet otherwise.</summary>
    public const float CounterBatteryBroadcastSeconds = 20f;

    /// <summary>
    /// Rebind delay after a full reset. Shorter than the routine retry on purpose: a reset is a
    /// deliberate act by the commander and the map is expected back immediately, whereas the
    /// routine interval exists to keep an unbindable scene from being hammered.
    /// </summary>
    public const float RebindAfterResetSeconds = 1f;

    /// <summary>Puts every beat back on the starting line; the next frame fires all of them.</summary>
    public void ResetAll()
    {
        Map.Reset();
        Telegraph.Reset();
        Fcs.Reset();
        Misc.Reset();
        Bind.Reset();
        CounterBattery.Reset();
    }

    /// <summary>
    /// One cadence. Not thread-safe and deliberately so: every beat is read and advanced from
    /// the Unity main thread inside OnUpdate, and a lock here would only hide a threading bug.
    /// </summary>
    public sealed class Tick
    {
        private readonly float _intervalSeconds;
        private float _dueAt;

        public Tick(float intervalSeconds) => _intervalSeconds = intervalSeconds;

        /// <summary>
        /// True once per interval, and advances the beat when it answers true. A frame that
        /// arrives late does not accumulate a backlog: the next due time is measured from now,
        /// so a stalled scene load costs one beat rather than a burst of them.
        /// </summary>
        public bool Due(float now)
        {
            if (now < _dueAt) return false;
            _dueAt = now + _intervalSeconds;
            return true;
        }

        /// <summary>Reads the beat without consuming it, for callers that schedule by hand.</summary>
        public bool IsDue(float now) => now >= _dueAt;

        /// <summary>Pins the next occurrence explicitly, overriding the interval this once.</summary>
        public void ScheduleIn(float now, float seconds) => _dueAt = now + seconds;

        /// <summary>Makes the beat due immediately.</summary>
        public void Reset() => _dueAt = 0f;
    }
}
