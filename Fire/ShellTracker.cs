using System.Text.RegularExpressions;
using IronNestAgentBridge.GameState;
using UnityEngine;

namespace IronNestAgentBridge.Fire;

/// <summary>
/// The bridge's own ledger of every fire mission it queued: which serials are still inside FCS,
/// which shells are in the air, and which of those have quietly landed.
///
/// The ledger exists because FCS answers a different question than the agent asks. FCS knows what
/// is queued; the agent needs to know whether a target has already been serviced, and re-queueing
/// a target that has a shell in the air wastes a round and a minute of gun time.
///
/// Two rules shape everything here:
/// <list type="bullet">
/// <item><b>Never parse an FCS display string to decide task state.</b> A serial leaving
/// <see cref="FcsStatusDto.SerialToMarker"/> means it left the active set;
/// <see cref="FcsStatusDto.RecentOutcomes"/> says whether that was a shot or a failure. The one
/// regex in this file reads back a <c>#N</c> the bridge itself printed.</item>
/// <item><b>A shell in flight is never allowed to expire silently.</b> Each gun owns exactly one
/// impact marker, so a second shell landing on the same spot moves nothing and produces no impact
/// signal at all. Timing one out therefore has to emit an event, or the agent waits forever.</item>
/// </list>
///
/// Main thread only: every method here reads live game state or appends events produced by it.
/// </summary>
public sealed class ShellTracker
{
    /// <summary>Absolute ceiling on how long a shell may be considered airborne. Seconds.</summary>
    public const float InFlightTimeoutSeconds = 150f;

    /// <summary>An impact this far from an in-flight shell's aim point is that shell's. Kilometres.</summary>
    public const float ImpactMatchKm = 3f;

    /// <summary>Second-stage throttle on the friendly-intrusion patrol, inside the 0.5s map tick.</summary>
    public const float FriendlyPatrolSeconds = 5f;

    /// <summary>Reads back the <c>#N</c> head of a task line the bridge printed itself.</summary>
    private static readonly Regex SerialPattern = new(@"^#(\d+)\b", RegexOptions.Compiled);

    /// <summary>
    /// One queued fire mission. Bound to a serial and a pair of coordinates — never to a map
    /// marker: T1–T8 belong to the player and T9/T10 to FCS, and the bridge moves neither.
    /// Mutable because a last-moment re-aim has to keep the impact-matching point fresh.
    /// </summary>
    public sealed class DeployedTask
    {
        public DeployedTask(int serial, string label, string shell, float kmX, float kmY, float flightEtaSeconds)
        {
            Serial = serial;
            Label = label;
            Shell = shell;
            KmX = kmX;
            KmY = kmY;
            FlightEtaSeconds = flightEtaSeconds;
        }

        public int Serial { get; }

        /// <summary>Human-facing target description, as the receipt and the events show it.</summary>
        public string Label { get; set; }

        public string Shell { get; }

        /// <summary>Aim point, km frame. Follows a re-aim.</summary>
        public float KmX { get; set; }
        public float KmY { get; set; }

        /// <summary>Estimated time of flight, used as the (shorter) settle deadline.</summary>
        public float FlightEtaSeconds { get; }
    }

    /// <summary>A shell that has left the barrel and has not been accounted for yet.</summary>
    public sealed record InFlightShell
    {
        public string Label { get; init; } = "";
        public string Shell { get; init; } = "";
        public float KmX { get; init; }
        public float KmY { get; init; }

        /// <summary><c>Time.realtimeSinceStartup</c> at the moment of firing.</summary>
        public float FiredAt { get; init; }

        /// <summary>Mission clock at the moment of firing; empty when no clock was running.</summary>
        public string FiredAtGame { get; init; } = "";

        public int Serial { get; init; }
        public float FlightEtaSeconds { get; init; } = 60f;
    }

    /// <summary>Serial to queued mission. The bridge's whole notion of "outstanding work".</summary>
    private readonly Dictionary<int, DeployedTask> _deployed = new();

    private readonly List<InFlightShell> _inFlight = new();

    /// <summary>Serials already warned about an intrusion, so each one is announced once.</summary>
    private readonly HashSet<int> _warnedIntrusions = new();

    private float _nextFriendlyPatrol;

    public bool HasDeployedTasks => _deployed.Count > 0;

    // ---------------------------------------------------------------- ledger maintenance

    /// <summary>
    /// Books a mission that FCS accepted. Only ever called with a real serial: a queue that came
    /// back without one is treated as a failure by the pipeline, precisely so that no untrackable
    /// entry can enter this ledger.
    /// </summary>
    public void Register(int serial, string label, string shell, float kmX, float kmY, float flightEtaSeconds)
    {
        if (serial <= 0) return;
        _deployed[serial] = new DeployedTask(serial, label, shell, kmX, kmY, flightEtaSeconds);
    }

    public bool TryGetDeployed(int serial, out DeployedTask? task) => _deployed.TryGetValue(serial, out task);

    /// <summary>Follows a last-moment re-aim so impact matching keeps using the real aim point.</summary>
    public void UpdateAim(int serial, string label, float kmX, float kmY)
    {
        if (!_deployed.TryGetValue(serial, out var task)) return;

        task.Label = label;
        task.KmX = kmX;
        task.KmY = kmY;
    }

    /// <summary>
    /// Drops a serial from the ledger without emitting anything. The cancel path calls this as
    /// belt and braces: FCS now records a cancellation in RecentTasks as
    /// <c>Failed: cancelled by commander</c>, so the fired/failed discrimination already handles
    /// it, but a cancelled task must not linger in the ledger even if that record is missed.
    /// </summary>
    public void Forget(int serial)
    {
        _deployed.Remove(serial);
        _warnedIntrusions.Remove(serial);
    }

    /// <summary>Full reset (F9 / new mission): the ledger describes a world that no longer exists.</summary>
    public void Clear()
    {
        _deployed.Clear();
        _inFlight.Clear();
        _warnedIntrusions.Clear();
        _nextFriendlyPatrol = 0f;
    }

    // ---------------------------------------------------------------- fired / failed

    /// <summary>
    /// Reconciles the ledger against the FCS active set (2s tick). A serial the ledger holds but
    /// <see cref="FcsStatusDto.SerialToMarker"/> no longer lists has left the queue and both guns:
    /// either it fired, or it died before firing. Getting that wrong in the "failed" direction
    /// leaves the agent waiting on a shell that never existed.
    /// </summary>
    public void TrackFiredShells(FcsStatusDto status)
    {
        if (_deployed.Count == 0) return;

        List<int>? departed = null;
        foreach (var serial in _deployed.Keys)
        {
            if (status.SerialToMarker.ContainsKey(serial)) continue;
            (departed ??= new List<int>()).Add(serial);
        }

        if (departed == null) return;

        var now = Il2CppSafe.Get(() => Time.realtimeSinceStartup, 0f);

        foreach (var serial in departed)
        {
            if (!_deployed.TryGetValue(serial, out var task)) continue;
            _deployed.Remove(serial);
            _warnedIntrusions.Remove(serial);

            if (status.RecentOutcomes.TryGetValue(serial, out var outcome) &&
                outcome.StartsWith("Failed", StringComparison.Ordinal))
            {
                // Split on the "Failed: " prefix rather than slicing a fixed width, so a receipt
                // whose prefix ever changes shape degrades to "unknown" instead of to garbage.
                var colon = outcome.IndexOf(':');
                var why = colon >= 0 ? outcome[(colon + 1)..].TrimStart() : "";
                if (why.Length == 0) why = "unknown";

                EventLog.Append("fcs_task_update", "fcs",
                    $"⚠任务失败(未发射): #{task.Serial} {task.Label} ({task.Shell}) — {why}。" +
                    "目标未被服务; 按失败原因处置(装药/射程问题就改打近目标或换弹, 而不是原样重排)");
                continue;
            }

            _inFlight.Add(new InFlightShell
            {
                Label = task.Label,
                Shell = task.Shell,
                KmX = task.KmX,
                KmY = task.KmY,
                FiredAt = now,
                FiredAtGame = EventLog.GameClock,
                Serial = task.Serial,
                FlightEtaSeconds = task.FlightEtaSeconds,
            });

            EventLog.Append("shell_fired", "fcs",
                $"炮弹出膛: #{task.Serial} {task.Label} ({task.Shell}) 已在飞行途中, 等待弹着 — 勿重复排队该目标{BalanceSuffix()}");
        }
    }

    // ---------------------------------------------------------------- landing

    /// <summary>
    /// Settles a real impact against the in-flight list. Handed to the impact reader as a callback.
    /// </summary>
    /// <returns>
    /// Identity of the shell that landed (<c>#12 K4 5:0 (HE)</c>), or null when nothing matches —
    /// the caller then reports the impact without naming a mission.
    /// </returns>
    public string? OnShellImpact(float kmX, float kmY)
    {
        var bestIndex = -1;
        var bestDistance = float.MaxValue;

        for (var i = 0; i < _inFlight.Count; i++)
        {
            var shell = _inFlight[i];
            var dx = shell.KmX - kmX;
            var dy = shell.KmY - kmY;
            var distance = MathF.Sqrt(dx * dx + dy * dy);

            if (distance >= ImpactMatchKm || distance >= bestDistance) continue;

            bestDistance = distance;
            bestIndex = i;
        }

        if (bestIndex < 0) return null;

        var settled = _inFlight[bestIndex];
        _inFlight.RemoveAt(bestIndex);
        return $"#{settled.Serial} {settled.Label} ({settled.Shell})";
    }

    /// <summary>
    /// Writes off shells whose flight time has run out (0.5s tick). The deadline is the shorter of
    /// the estimated time of flight and the absolute ceiling.
    ///
    /// This must emit an event, never expire quietly: repeat fire onto the same point does not
    /// move the gun's single impact marker, so a second impact signal is physically impossible and
    /// an agent that keeps waiting for one will stall on that target for the rest of the mission.
    /// </summary>
    public void ResolveOverdueShells()
    {
        if (_inFlight.Count == 0) return;

        var now = Il2CppSafe.Get(() => Time.realtimeSinceStartup, 0f);

        for (var i = _inFlight.Count - 1; i >= 0; i--)
        {
            var shell = _inFlight[i];
            var deadline = MathF.Min(shell.FlightEtaSeconds, InFlightTimeoutSeconds);
            if (now - shell.FiredAt <= deadline) continue;

            _inFlight.RemoveAt(i);

            EventLog.Append("shell_impact", "map",
                $"弹着推定: #{shell.Serial} {shell.Label} ({shell.Shell}) 已超预计飞行时间, 判定已落地并销账 — " +
                "弹着标记未移动通常=与前一发落点几乎重合; 可重新评估该目标");
        }
    }

    /// <summary>In-flight roster for the snapshot; one line per shell, oldest first.</summary>
    public List<string> DescribeInFlight()
    {
        var lines = new List<string>(_inFlight.Count);
        if (_inFlight.Count == 0) return lines;

        var now = Il2CppSafe.Get(() => Time.realtimeSinceStartup, 0f);

        foreach (var shell in _inFlight)
        {
            var firedAt = shell.FiredAtGame.Length > 0 ? shell.FiredAtGame : "?";
            lines.Add(
                $"#{shell.Serial} {shell.Label} ({shell.Shell}, 出膛@{firedAt}, " +
                $"已飞{now - shell.FiredAt:F0}s/预计{shell.FlightEtaSeconds:F0}s)");
        }

        return lines;
    }

    /// <summary>
    /// Appends the bridge's own target label to an FCS task line, so the snapshot reads
    /// "#12 HE brg … → K4 5:0" instead of leaving the agent to remember what #12 was aimed at.
    /// The serial is read out of the <c>#N</c> head the bridge itself formats; no other part of
    /// the display string is interpreted.
    /// </summary>
    public string? AnnotateTask(string? description)
    {
        if (description == null) return null;

        var match = SerialPattern.Match(description);
        if (!match.Success) return description;

        if (!int.TryParse(match.Groups[1].Value, out var serial)) return description;
        if (!_deployed.TryGetValue(serial, out var task)) return description;

        return $"{description} → {task.Label}";
    }

    // ---------------------------------------------------------------- standing patrol

    /// <summary>
    /// Re-surveys the impact area of every queued mission (5s, inside the 0.5s map tick).
    ///
    /// The queue-time survey only describes the instant of queueing, and a queued task can wait
    /// fifteen minutes while the front line walks into its impact area. Without this patrol the
    /// bridge would happily drop a shell on troops that were nowhere near the point when the
    /// mission was accepted.
    /// </summary>
    /// <param name="now"><c>Time.realtimeSinceStartup</c>, sampled once per frame by the caller.</param>
    /// <param name="map">Bound map reader; the patrol is skipped while unbound.</param>
    public void PollFriendlyIntrusions(float now, MapReader map)
    {
        if (now < _nextFriendlyPatrol) return;
        _nextFriendlyPatrol = now + FriendlyPatrolSeconds;

        if (_deployed.Count == 0 || !map.IsBound) return;

        List<MapEntityDto> entities;
        List<ShellSpecDto> specs;
        try
        {
            entities = map.ReadEntities();
            specs = AmmoReader.ReadShellSpecs();
        }
        catch
        {
            // A failed read is not evidence of safety, but it is also not evidence of danger:
            // skip this round and try again in five seconds.
            return;
        }

        foreach (var pair in _deployed)
        {
            var task = pair.Value;
            if (BlastSurvey.IsHarmless(task.Shell)) continue;

            var blastKm = BlastSurvey.BlastRadiusKm(task.Shell, specs);
            if (blastKm <= BlastSurvey.MinBlastKm) continue;

            var intruders = new List<string>();
            foreach (var entity in entities)
            {
                if (!entity.IsAlive || !BlastSurvey.IsProtected(entity)) continue;
                if (BlastSurvey.DistanceToImpactKm(entity, task.KmX, task.KmY) <= blastKm) intruders.Add(entity.Id);
            }

            if (intruders.Count == 0)
            {
                // Cleared: the same task may raise a fresh alarm if someone walks back in.
                _warnedIntrusions.Remove(task.Serial);
                continue;
            }

            if (!_warnedIntrusions.Add(task.Serial)) continue;

            EventLog.Append("friendly_warning", "map",
                $"⚠误伤预警: 已排任务 #{task.Serial} {task.Label} 的弹着区({task.Shell}半径{blastKm * 1000f:F0}m)内现有友军 " +
                $"{string.Join(", ", intruders)} — 立即adjust_fire挪开弹着点或cancel_pending_task");
        }

        SweepWarnings();
    }

    /// <summary>Forgets alarms for serials that have left the ledger.</summary>
    private void SweepWarnings()
    {
        if (_warnedIntrusions.Count == 0) return;

        List<int>? stale = null;
        foreach (var serial in _warnedIntrusions)
        {
            if (!_deployed.ContainsKey(serial)) (stale ??= new List<int>()).Add(serial);
        }

        if (stale == null) return;
        foreach (var serial in stale) _warnedIntrusions.Remove(serial);
    }

    // ---------------------------------------------------------------- requisition balance

    /// <summary>
    /// Requisition balance stamped onto events that sit next to a purchase, so the agent always
    /// budgets against fresh money: every shell fired and every card bought moves the balance.
    /// Shared with the mod's card-completion event.
    /// </summary>
    /// <returns>" · 征用点余额 N", or an empty string when the balance cannot be read.</returns>
    public static string BalanceSuffix()
    {
        var points = AmmoReader.ReadRequisitionPoints();
        return points.HasValue ? $" · 征用点余额 {points.Value}" : "";
    }
}
