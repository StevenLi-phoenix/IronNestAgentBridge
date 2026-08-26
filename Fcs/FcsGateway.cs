using System.Collections;
using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace IronNestAgentBridge.Fcs;

/// <summary>
/// The one and only seam between this bridge and the fire-control mod "IronNestFCS Smart".
/// Everything here goes through reflection: the bridge assembly must never reference the FCS
/// Logic assembly, name one of its types statically, or hold on to a <see cref="Type"/>,
/// <see cref="MethodInfo"/>, <see cref="FieldInfo"/> or instance across a reload.
///
/// FCS Logic lives in a collectable AssemblyLoadContext that is torn down and rebuilt on every
/// F9 and every scene load. The only cache allowed is "the module instance last seen plus the
/// FSC taken out of it", validated by reference on every single call (see <see cref="Resolve"/>).
///
/// One instance per mod, main thread only: every member below touches live game state through
/// FCS, so callers must come in through <c>MainThread.Run</c>. The cache is therefore lock-free.
///
/// Capability probing, never version checks: the difference between stock FCS and this
/// project's fork is decided by whether a field or method exists.
/// </summary>
public sealed class FcsGateway
{
    /// <summary>Exact melon name of the host mod; matched with ordinal equality, not a substring.</summary>
    private const string FcsModName = "IronNestFCS Smart";

    /// <summary>Every read goes through this (§3.4-10).</summary>
    private const BindingFlags AnyInstance =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Every write into a public contract field goes through this (§3.4-10).</summary>
    private const BindingFlags PublicInstance = BindingFlags.Instance | BindingFlags.Public;

    // Coordinate protocol, shared with the bridge's own map math: Draggable Surface local
    // units × 3.8164 = km, km frame origin offset (10.016, 5.235).
    private const float LocalToKmScale = 3.8164f;
    private const float KmFrameOriginX = 10.016f;
    private const float KmFrameOriginY = 5.235f;

    // Fully qualified names inside the FCS Logic assembly. Resolved against the assembly of the
    // *current* FSC instance so the types land in the ALC that is alive right now.
    private const string ArtilleryTaskTypeName = "IronNestFCS.Logic.FCS.ArtilleryTask";
    private const string BulletTypeTypeName = "IronNestFCS.Logic.FCS.BulletType";

    private const string ErrModMissing = "IronNestFCS Smart mod not present";
    private const string ErrLogicMissing = "FCS Logic not loaded (scene not bound yet?)";
    private const string ErrFscMissing = "FCS instance unavailable";
    private const string ErrTypesMissing = "FCS internal types not found (incompatible FCS version?)";

    /// <summary>Last module instance seen, used purely as the identity half of the cache.</summary>
    private object? _cachedModule;

    /// <summary>FSC taken out of <see cref="_cachedModule"/>; dropped the moment either changes.</summary>
    private object? _cachedFsc;

    /// <summary>Members already reported by <see cref="WarnOnce"/>; one line each, ever.</summary>
    private readonly HashSet<string> _warnedMembers = new(StringComparer.Ordinal);

    /// <summary>
    /// Linear motion model of a moving target, expressed in map-local space (not km):
    /// <c>p(t) = origin + vel · (t − t0)</c>, velocity in local units per second and
    /// <paramref name="T0Seconds"/> on the mission clock. Injected into a task so FCS can lead
    /// the target itself — the LLM is never allowed to compute lead by hand.
    /// </summary>
    public sealed record MotionSpec(
        float OriginLocalX,
        float OriginLocalY,
        float VelLocalX,
        float VelLocalY,
        float T0Seconds);

    /// <summary>Outcome of handing a punch-card purchase to the FCS console coordinator.</summary>
    public enum CardPurchaseStatus
    {
        /// <summary>No usable FSC instance: mod absent, Logic unloaded, or scene not bound.</summary>
        NoFcs,

        /// <summary>FSC is alive but exposes no <c>RequestConsoleCard</c> overload (stock FCS).</summary>
        NoApi,

        /// <summary>FCS accepted the request; <see cref="CardPurchaseResult.Message"/> is its receipt.</summary>
        Queued,
    }

    /// <summary>
    /// Structured result of <see cref="RequestCardPurchase"/> (§3.4-6). Both non-queued states
    /// mean "fall back to the bridge's own physical purchase", but they are different faults and
    /// the caller gets to say which one it hit instead of inferring it from a null.
    /// </summary>
    public sealed record CardPurchaseResult(CardPurchaseStatus Status, string Message)
    {
        public static readonly CardPurchaseResult NoFcs = new(CardPurchaseStatus.NoFcs, "");
        public static readonly CardPurchaseResult NoApi = new(CardPurchaseStatus.NoApi, "");

        /// <summary>FCS returning a null receipt still counts as accepted; the text is just empty.</summary>
        public static CardPurchaseResult Queued(string? message) =>
            new(CardPurchaseStatus.Queued, message ?? "");

        public bool Accepted => Status == CardPurchaseStatus.Queued;
    }

    // ---------------------------------------------------------------------------------------
    // Resolution chain
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Walks melon → <c>_reloader</c> → <c>Current</c> → <c>_fcs</c> from scratch, every call.
    /// A failure at any level clears the cache (§3.4-1): a half-valid cache outlives exactly the
    /// situation it was meant to survive. <c>_fcs</c> is re-read every time and compared by
    /// reference (§3.4-2), so a hot-swapped Logic build is picked up without a restart.
    /// </summary>
    /// <param name="modPresent">A melon named <see cref="FcsModName"/> exists, loaded Logic or not.</param>
    /// <param name="logicLoaded">The reloader handed out a live module instance.</param>
    private object? Resolve(out bool modPresent, out bool logicLoaded)
    {
        modPresent = false;
        logicLoaded = false;

        object? host = null;
        try
        {
            foreach (var melon in MelonMod.RegisteredMelons)
            {
                if (melon?.Info == null || melon.Info.Name != FcsModName) continue;
                host = melon;
                break;
            }
        }
        catch
        {
            host = null;
        }

        // No FCS installed at all is a configuration, not an ABI mismatch: no diagnostic.
        if (host == null)
        {
            ClearCache();
            return null;
        }

        modPresent = true;

        var reloader = ReadMember(host, "_reloader", "FcsHostMod._reloader");
        if (reloader == null)
        {
            ClearCache();
            return null;
        }

        var module = ReadMember(reloader, "Current", "LogicReloader.Current");
        if (module == null)
        {
            ClearCache();
            return null;
        }

        logicLoaded = true;

        var fsc = ReadMember(module, "_fcs", "FcsModule._fcs");
        if (fsc == null)
        {
            ClearCache();
            return null;
        }

        if (!ReferenceEquals(module, _cachedModule) || !ReferenceEquals(fsc, _cachedFsc))
        {
            _cachedModule = module;
            _cachedFsc = fsc;
        }

        return fsc;
    }

    private void ClearCache()
    {
        _cachedModule = null;
        _cachedFsc = null;
    }

    /// <summary>Resolution plus the diagnostic string shared by the enqueue paths and adjust.</summary>
    private object? ResolveOrExplain(out string error)
    {
        var fsc = Resolve(out var modPresent, out var logicLoaded);
        if (fsc != null)
        {
            error = "";
            return fsc;
        }

        error = !modPresent ? ErrModMissing
            : !logicLoaded ? ErrLogicMissing
            : ErrFscMissing;
        return null;
    }

    // ---------------------------------------------------------------------------------------
    // Status
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The single status read: <c>GET /state</c>, the F10 panel and the agent snapshot all get
    /// this DTO. Never returns null and never throws — an unreadable property only costs its own
    /// field, and an unresolvable FCS still reports <c>ModPresent</c> / <c>LogicLoaded</c>.
    /// </summary>
    public FcsStatusDto ReadStatus()
    {
        var dto = new FcsStatusDto();

        var fsc = Resolve(out var modPresent, out var logicLoaded);
        dto.ModPresent = modPresent;
        dto.LogicLoaded = logicLoaded;
        if (fsc == null) return dto;

        dto.Bound = ReadValue(fsc, "IsBound", false, "FSC.IsBound");
        dto.PendingCount = ReadValue(fsc, "PendingCount", 0, "FSC.PendingCount");
        dto.AutoFireEnabled = ReadValue(fsc, "AutoFireEnabled", false, "FSC.AutoFireEnabled");
        dto.MaxChargeEnabled = ReadValue(fsc, "MaxChargeEnabled", false, "FSC.MaxChargeEnabled");
        dto.CompletedTaskCount = ReadValue(fsc, "CompletedTaskCount", 0, "FSC.CompletedTaskCount");
        dto.SuccessfulTaskCount = ReadValue(fsc, "SuccessfulTaskCount", 0, "FSC.SuccessfulTaskCount");
        dto.FailedTaskCount = ReadValue(fsc, "FailedTaskCount", 0, "FSC.FailedTaskCount");

        var left = ReadMember(fsc, "LeftTask", "FSC.LeftTask");
        dto.LeftTask = DescribeTask(left);
        RegisterSerial(dto, left);

        var right = ReadMember(fsc, "RightTask", "FSC.RightTask");
        dto.RightTask = DescribeTask(right);
        RegisterSerial(dto, right);

        // The queue as a whole is guarded: a throw mid-enumeration abandons the rest of the
        // collection without touching fields that are already filled in.
        try
        {
            if (ReadMember(fsc, "QueueCan", "FSC.QueueCan") is IEnumerable queue)
            {
                foreach (var task in queue)
                {
                    if (task == null) continue;
                    var line = DescribeTask(task);
                    if (line != null) dto.PendingTasks.Add(line);
                    RegisterSerial(dto, task);
                }
            }
        }
        catch
        {
            // partial queue is still worth reporting
        }

        try
        {
            if (ReadMember(fsc, "RecentTasks", "FSC.RecentTasks") is IEnumerable recent)
            {
                foreach (var task in recent)
                {
                    if (task == null) continue;
                    RegisterOutcome(dto, task);
                }
            }
        }
        catch
        {
            // same
        }

        return dto;
    }

    /// <summary>
    /// Registers one live task in the serial → marker table. The keys of that table are the
    /// bridge's definition of "not out of the barrel yet": a serial the bookkeeping still holds
    /// but this table no longer lists has been fired.
    /// </summary>
    private void RegisterSerial(FcsStatusDto dto, object? task)
    {
        if (task == null) return;
        try
        {
            if (ReadMember(task, "serial", "ArtilleryTask.serial") is int serial && serial > 0 &&
                ReadMember(task, "targetId", "ArtilleryTask.targetId") is int targetId)
            {
                dto.SerialToMarker[serial] = targetId;
            }
        }
        catch
        {
            // one unreadable task must not cost the whole table
        }
    }

    /// <summary>
    /// Folds one entry of <c>RecentTasks</c> into <c>RecentOutcomes</c>. "Failed" and the
    /// "Failed: " prefix are protocol literals: downstream tells a fired shell from a task that
    /// died before firing by them. Failure is decided by belt and braces (§3.4-9) — either the
    /// progress says Failed or a failure reason is present.
    /// </summary>
    private void RegisterOutcome(FcsStatusDto dto, object task)
    {
        try
        {
            if (ReadMember(task, "serial", "ArtilleryTask.serial") is not int serial || serial <= 0) return;

            var progress = ReadMember(task, "progress", "ArtilleryTask.progress")?.ToString() ?? "";
            var reason = ReadMember(task, "failureReason", "ArtilleryTask.failureReason") as string ?? "";

            dto.RecentOutcomes[serial] = progress == "Failed" || reason.Length > 0
                ? $"Failed: {reason}"
                : progress;
        }
        catch
        {
            // skip this entry only
        }
    }

    /// <summary>
    /// Renders a task for human eyes (HUD, panel, snapshot text). Display only: the bridge is
    /// never allowed to parse this back — serials and marker ids come from the structured tables.
    /// </summary>
    private string? DescribeTask(object? task)
    {
        if (task == null) return null;

        try
        {
            // Fork-only decoration; stock FCS has no MotionSuffix and simply gets no suffix.
            var motionSuffix = "";
            try
            {
                var suffixMethod = task.GetType().GetMethod(
                    "MotionSuffix", AnyInstance, null, new[] { typeof(bool) }, null);
                if (suffixMethod == null)
                {
                    WarnOnce("ArtilleryTask.MotionSuffix", "ArtilleryTask.MotionSuffix(bool) not found — motion suffixes disabled");
                }
                else
                {
                    motionSuffix = suffixMethod.Invoke(task, new object[] { true }) as string ?? "";
                }
            }
            catch
            {
                motionSuffix = "";
            }

            // Stock FCS has no serial, so the head falls back to the recycled marker number.
            var serial = ReadMember(task, "serial", "ArtilleryTask.serial") is int s ? s : 0;
            var targetId = ReadMember(task, "targetId", "ArtilleryTask.targetId") is int t ? t : 0;
            var head = serial > 0 ? $"#{serial}" : $"T{targetId}";

            var bulletType = ReadMember(task, "bulletType", "ArtilleryTask.bulletType");
            var angel = ReadMember(task, "angel", "ArtilleryTask.angel") is float a ? a : 0f;
            var distance = ReadMember(task, "distance", "ArtilleryTask.distance") is float d ? d : 0f;
            var chargeCount = ReadMember(task, "chargeCount", "ArtilleryTask.chargeCount") is int c ? c : 0;
            var progress = ReadMember(task, "progress", "ArtilleryTask.progress");

            // angel = bearing in degrees (1 dp), distance = km (2 dp), chargeCount = powder bags.
            var text =
                $"{head} {bulletType} brg {angel:F1} dist {distance:F2}km chg {chargeCount} [{progress}]{motionSuffix}";

            var failureReason = ReadMember(task, "failureReason", "ArtilleryTask.failureReason") as string;
            if (!string.IsNullOrEmpty(failureReason)) text += $" fail: {failureReason}";

            return text;
        }
        catch
        {
            return task.ToString();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Enqueue
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The authoritative enqueue path: a pure coordinate task that binds to no map marker at all.
    /// FCS re-solves it from <c>aimLocal</c> on every planning round, so the player's T1–T8
    /// markers are never touched by the bridge.
    /// </summary>
    /// <param name="localX">Aim point in Draggable Surface local units.</param>
    /// <param name="localY">Aim point in Draggable Surface local units.</param>
    /// <param name="bearingDeg">Bearing from the turret, degrees.</param>
    /// <param name="distanceKm">Range from the turret, km.</param>
    /// <param name="shell">Bullet type name, case-insensitive.</param>
    /// <param name="priority">0–100; 90 and above skips the batching window.</param>
    /// <param name="serial">FCS-assigned task serial, or -1 on any failure. The bridge's only handle.</param>
    /// <param name="trackEntityId">Entity for FCS to track and sample itself; null or empty to skip.</param>
    /// <param name="motion">LLM-transcribed linear motion model; null to skip.</param>
    /// <param name="validForSeconds">Queue lifetime; only values &gt; 0 are written.</param>
    /// <returns><c>"ok"</c> on success, otherwise a diagnostic string.</returns>
    public string EnqueueAimPoint(
        float localX,
        float localY,
        float bearingDeg,
        float distanceKm,
        string shell,
        int priority,
        out int serial,
        string? trackEntityId = null,
        MotionSpec? motion = null,
        float? validForSeconds = null)
    {
        serial = -1;

        var fsc = ResolveOrExplain(out var error);
        if (fsc == null) return error;

        var (taskType, bulletEnumType) = LogicTypes(fsc);
        if (taskType == null || bulletEnumType == null) return ErrTypesMissing;

        // Capability probe: aim-point tasks are a fork feature and the whole marker-free regime
        // depends on them, so this one is a hard stop rather than a silent degrade.
        if (taskType.GetField("hasAimPoint", PublicInstance) == null)
            return "FCS build lacks aim-point tasks — update the FCS fork";

        if (!TryParseShell(bulletEnumType, shell, out var bullet)) return $"unknown shell type '{shell}'";

        var task = Activator.CreateInstance(taskType)!;

        SetRequired(taskType, task, "targetId", 0); // 0 = bound to no map marker
        SetRequired(taskType, task, "angel", bearingDeg);
        SetRequired(taskType, task, "distance", distanceKm);
        SetRequired(taskType, task, "position", LocalToKmFrame(localX, localY));
        SetRequired(taskType, task, "bulletType", bullet);
        SetRequired(taskType, task, "hasAimPoint", true);
        SetRequired(taskType, task, "aimLocal", new Vector3(localX, localY, 0f)); // local frame, unconverted

        TrySetPriority(taskType, task, priority);
        TrySetMotion(taskType, task, trackEntityId, motion);

        // A lifetime of 0 or less means "no lifetime", not "expires immediately".
        if (validForSeconds is > 0f)
            TrySet(taskType, task, "validForSeconds", validForSeconds.Value, "ArtilleryTask.validForSeconds");

        var enqueue = FindMethod(fsc.GetType(), "EnqueueTask", taskType);
        if (enqueue == null) return "FSC.EnqueueTask not found";

        try
        {
            enqueue.Invoke(fsc, new[] { task });
        }
        catch (Exception ex)
        {
            return CallFailed("FSC.EnqueueTask", ex);
        }

        // FCS assigns the serial during enqueue; this read-back is where the bridge picks up the
        // handle it will use for adjust, cancel and impact bookkeeping.
        try
        {
            if (ReadMember(task, "serial", "ArtilleryTask.serial") is int assigned) serial = assigned;
        }
        catch
        {
            serial = -1;
        }

        return "ok";
    }

    /// <summary>
    /// Marker-bound enqueue by bearing and range. Currently has no callers; kept because it is
    /// the only path that hands FCS a <c>targetId</c> the player can see.
    ///
    /// §3.4-3: the local aim point is a parameter here as well, so <c>position</c> carries real
    /// km-frame coordinates instead of the <c>Vector3.zero</c> placeholder it used to.
    /// </summary>
    public string EnqueueByBearing(
        float localX,
        float localY,
        float bearingDeg,
        float distanceKm,
        string shell,
        int targetId,
        int priority = 50)
    {
        var fsc = ResolveOrExplain(out var error);
        if (fsc == null) return error;

        var (taskType, bulletEnumType) = LogicTypes(fsc);
        if (taskType == null || bulletEnumType == null) return ErrTypesMissing;

        if (!TryParseShell(bulletEnumType, shell, out var bullet)) return $"unknown shell type '{shell}'";

        var task = Activator.CreateInstance(taskType)!;

        SetRequired(taskType, task, "targetId", targetId);
        SetRequired(taskType, task, "angel", bearingDeg);
        SetRequired(taskType, task, "distance", distanceKm);
        SetRequired(taskType, task, "position", LocalToKmFrame(localX, localY));
        SetRequired(taskType, task, "bulletType", bullet);

        TrySetPriority(taskType, task, priority);

        var enqueue = FindMethod(fsc.GetType(), "EnqueueTask", taskType);
        if (enqueue == null) return "FSC.EnqueueTask not found";

        try
        {
            enqueue.Invoke(fsc, new[] { task });
        }
        catch (Exception ex)
        {
            return CallFailed("FSC.EnqueueTask", ex);
        }

        // No serial read-back on this path: marker-bound tasks are not tracked by the bridge.
        return "ok";
    }

    /// <summary>
    /// Writes the tracking id and the linear motion model. All of these fields exist only on the
    /// patched fork; on stock FCS the whole block is silently skipped so the task still enqueues.
    /// </summary>
    private void TrySetMotion(Type taskType, object task, string? trackEntityId, MotionSpec? motion)
    {
        try
        {
            if (!string.IsNullOrEmpty(trackEntityId))
                TrySet(taskType, task, "trackEntityId", trackEntityId, "ArtilleryTask.trackEntityId");

            if (motion == null) return;

            TrySet(taskType, task, "hasMotion", true, "ArtilleryTask.hasMotion");
            TrySet(taskType, task, "motionOriginLocal",
                new Vector3(motion.OriginLocalX, motion.OriginLocalY, 0f), "ArtilleryTask.motionOriginLocal");
            TrySet(taskType, task, "motionVelLocalPerSec",
                new Vector3(motion.VelLocalX, motion.VelLocalY, 0f), "ArtilleryTask.motionVelLocalPerSec");
            TrySet(taskType, task, "motionT0", motion.T0Seconds, "ArtilleryTask.motionT0");
        }
        catch
        {
            // motion is a bonus, never a precondition for firing
        }
    }

    /// <summary>
    /// Writes task priority (0–100). Fork-only; stock FCS ignores priority entirely, so a missing
    /// field is a degrade, not a failure. Semantics live elsewhere: P≥90 skips the batching
    /// window, P100 preempts a gun.
    /// </summary>
    private void TrySetPriority(Type taskType, object task, int priority) =>
        TrySet(taskType, task, "priority", priority, "ArtilleryTask.priority");

    // ---------------------------------------------------------------------------------------
    // Other FSC capabilities
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Last-moment re-aim of a task that is queued or already loading. Non-blocking by design:
    /// FCS never waits for a re-aim, its staged re-solve pipeline picks the new point up at the
    /// next opportunity and otherwise fires on the original one.
    /// </summary>
    /// <param name="localX">New aim point, map-local frame (same frame as <c>aimLocal</c>).</param>
    /// <param name="localY">New aim point, map-local frame.</param>
    public string AdjustTaskAim(int serial, float localX, float localY)
    {
        var fsc = ResolveOrExplain(out var error);
        if (fsc == null) return error;

        var adjust = FindMethod(fsc.GetType(), "AdjustTaskAim", typeof(int), typeof(float), typeof(float));
        if (adjust == null)
        {
            WarnOnce("FSC.AdjustTaskAim", "FSC.AdjustTaskAim not found — re-aim unavailable on this FCS build");
            return "FCS build lacks AdjustTaskAim";
        }

        try
        {
            return adjust.Invoke(fsc, new object[] { serial, localX, localY }) as string ?? "adjust failed";
        }
        catch (Exception ex)
        {
            return CallFailed("FSC.AdjustTaskAim", ex);
        }
    }

    /// <summary>
    /// Cancels a task that has not started executing yet; a task already on a gun is not
    /// cancellable this way. Note the diagnostic strings deliberately differ from the enqueue
    /// paths' — they are a separate, shorter set and downstream text depends on the difference.
    /// </summary>
    public string CancelPending(int serial)
    {
        var fsc = Resolve(out var modPresent, out var logicLoaded);
        if (fsc == null)
        {
            return !modPresent ? "FCS mod not present"
                : !logicLoaded ? "FCS logic not loaded"
                : "FCS unavailable";
        }

        var cancel = FindMethod(fsc.GetType(), "CancelPendingTask", typeof(int));
        if (cancel == null)
        {
            WarnOnce("FSC.CancelPendingTask", "FSC.CancelPendingTask not found — cancelling unavailable on this FCS build");
            return "FCS build lacks CancelPendingTask";
        }

        try
        {
            var cancelled = cancel.Invoke(fsc, new object[] { serial }) as string;
            return cancelled == null ? $"no pending task with #{serial}" : $"cancelled: {cancelled}";
        }
        catch (Exception ex)
        {
            return CallFailed("FSC.CancelPendingTask", ex);
        }
    }

    /// <summary>
    /// Resolves <c>#N</c> back into shell type and internal marker id by scanning the live task
    /// set (left gun, right gun, queue) — structurally, never by parsing a display string.
    ///
    /// A true return with <paramref name="shell"/> still null is legal and expected when the task
    /// carries no readable bullet type; the caller is responsible for its own fallback.
    /// </summary>
    public bool TryGetTaskInfo(int serial, out string? shell, out int markerId)
    {
        shell = null;
        markerId = -1;

        var fsc = Resolve(out _, out _);
        if (fsc == null) return false;

        try
        {
            var task = FindTaskBySerial(fsc, serial);
            if (task == null) return false;

            shell = ReadMember(task, "bulletType", "ArtilleryTask.bulletType")?.ToString();
            markerId = ReadMember(task, "targetId", "ArtilleryTask.targetId") is int id ? id : -1;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>First live task whose serial matches exactly: left gun, right gun, then the queue.</summary>
    private object? FindTaskBySerial(object fsc, int serial)
    {
        var left = ReadMember(fsc, "LeftTask", "FSC.LeftTask");
        if (MatchesSerial(left, serial)) return left;

        var right = ReadMember(fsc, "RightTask", "FSC.RightTask");
        if (MatchesSerial(right, serial)) return right;

        if (ReadMember(fsc, "QueueCan", "FSC.QueueCan") is not IEnumerable queue) return null;

        foreach (var task in queue)
        {
            if (MatchesSerial(task, serial)) return task;
        }

        return null;
    }

    private bool MatchesSerial(object? task, int serial) =>
        task != null && ReadMember(task, "serial", "ArtilleryTask.serial") is int s && s == serial;

    /// <summary>
    /// The FCS console lock (<c>SharedResources.Requisition</c>, a <c>CoroutineLock</c> inside the
    /// Logic ALC). Fetched fresh on every call and never cached across an F9.
    ///
    /// Null means "no FCS", and the caller's contract for null is to run the requisition console
    /// unlocked — there is nobody else to contend with. Callers acquire it by reflecting
    /// <c>Acquire</c> with <c>Type.EmptyTypes</c>; the overloaded form must not be picked up.
    /// </summary>
    public object? GetRequisitionLock()
    {
        var fsc = Resolve(out _, out _);
        if (fsc == null) return null;

        var shared = ReadMember(fsc, "SharedResources", "FSC.SharedResources");
        if (shared == null) return null;

        return ReadMember(shared, "Requisition", "SharedResources.Requisition");
    }

    /// <summary>
    /// Submits a punch-card purchase to the FCS console coordinator as a DTO, so the bridge no
    /// longer holds the console lock itself. Overloads are probed in order of capability and the
    /// first hit wins; presence and absence of bearing/range travel as (value, hasValue) pairs.
    /// </summary>
    /// <returns>
    /// <see cref="CardPurchaseStatus.Queued"/> with the FCS receipt, or NoFcs / NoApi telling the
    /// caller to fall back to the bridge's physical purchase simulation.
    /// </returns>
    public CardPurchaseResult RequestCardPurchase(
        string cardId,
        float? bearingDeg = null,
        int priority = 50,
        string? startGrid = null,
        float? distanceKm = null)
    {
        var fsc = Resolve(out _, out _);
        if (fsc == null) return CardPurchaseResult.NoFcs;

        var overloads = new (Type[] Types, object?[] Args)[]
        {
            (new[] { typeof(string), typeof(float), typeof(bool), typeof(float), typeof(bool), typeof(int), typeof(string) },
                new object?[] { cardId, bearingDeg ?? 0f, bearingDeg.HasValue, distanceKm ?? 0f, distanceKm.HasValue, priority, startGrid }),
            (new[] { typeof(string), typeof(float), typeof(bool), typeof(int), typeof(string) },
                new object?[] { cardId, bearingDeg ?? 0f, bearingDeg.HasValue, priority, startGrid }),
            (new[] { typeof(string), typeof(float), typeof(bool), typeof(int) },
                new object?[] { cardId, bearingDeg ?? 0f, bearingDeg.HasValue, priority }),
            (new[] { typeof(string), typeof(float), typeof(bool) },
                new object?[] { cardId, bearingDeg ?? 0f, bearingDeg.HasValue }),
        };

        var type = fsc.GetType();
        foreach (var (types, args) in overloads)
        {
            MethodInfo? method = null;
            try
            {
                method = type.GetMethod("RequestConsoleCard", AnyInstance, null, types, null);
            }
            catch
            {
                method = null;
            }

            if (method == null) continue;

            try
            {
                return CardPurchaseResult.Queued(method.Invoke(fsc, args) as string);
            }
            catch (Exception ex)
            {
                // The coordinator is there but refused the call: fall back rather than lose the card.
                CallFailed("FSC.RequestConsoleCard", ex);
                return CardPurchaseResult.NoApi;
            }
        }

        WarnOnce("FSC.RequestConsoleCard",
            "FSC.RequestConsoleCard not found (no known overload) — falling back to the bridge's physical purchase");
        return CardPurchaseResult.NoApi;
    }

    /// <summary>Latest console card receipt, polled by the bridge; null when FCS is unavailable.</summary>
    public string? ReadConsoleCardResult()
    {
        var fsc = Resolve(out _, out _);
        if (fsc == null) return null;

        return ReadMember(fsc, "ConsoleCardRequestResult", "FSC.ConsoleCardRequestResult") as string;
    }

    // ---------------------------------------------------------------------------------------
    // Reflection plumbing
    // ---------------------------------------------------------------------------------------

    /// <summary>Draggable Surface local units → km frame. Coordinate protocol constants, verbatim.</summary>
    private static Vector3 LocalToKmFrame(float localX, float localY) => new(
        KmFrameOriginX + localX * LocalToKmScale,
        KmFrameOriginY + localY * LocalToKmScale,
        0f);

    /// <summary>
    /// The two Logic types, taken from the assembly of the current FSC instance so they belong to
    /// the ALC that is alive right now. Never cached.
    /// </summary>
    private (Type? Task, Type? Bullet) LogicTypes(object fsc)
    {
        try
        {
            var assembly = fsc.GetType().Assembly;
            var taskType = assembly.GetType(ArtilleryTaskTypeName);
            var bulletType = assembly.GetType(BulletTypeTypeName);

            if (taskType == null) WarnOnce("type:ArtilleryTask", $"{ArtilleryTaskTypeName} not found in {assembly.GetName().Name}");
            if (bulletType == null) WarnOnce("type:BulletType", $"{BulletTypeTypeName} not found in {assembly.GetName().Name}");

            return (taskType, bulletType);
        }
        catch (Exception ex)
        {
            WarnOnce("type:LogicAssembly", $"resolving FCS Logic types threw: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>Case-insensitive bullet type parse; an unknown name is a caller error, not a fault.</summary>
    private static bool TryParseShell(Type bulletEnumType, string shell, out object? value)
    {
        try
        {
            value = Enum.Parse(bulletEnumType, shell, ignoreCase: true);
            return true;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    /// <summary>
    /// Reads a property or field by name (property first, per the SharedResources rule), walking
    /// base types because NonPublic lookups do not inherit. A member that does not exist gets one
    /// diagnostic line ever; a member that exists and is simply null does not — null is a state,
    /// not an ABI mismatch.
    /// </summary>
    private object? ReadMember(object target, string name, string diagnosticKey)
    {
        try
        {
            var type = target.GetType();
            for (var t = type; t != null; t = t.BaseType)
            {
                var property = t.GetProperty(name, AnyInstance);
                if (property != null) return property.GetValue(target);

                var field = t.GetField(name, AnyInstance);
                if (field != null) return field.GetValue(target);
            }

            WarnOnce(diagnosticKey, $"member '{name}' not found on {type.FullName}");
            return null;
        }
        catch (Exception ex)
        {
            WarnOnce(diagnosticKey, $"reading '{name}' threw: {Unwrap(ex).Message}");
            return null;
        }
    }

    /// <summary>Typed read with a fallback; a wrong runtime type degrades to the fallback silently.</summary>
    private T ReadValue<T>(object target, string name, T fallback, string diagnosticKey) =>
        ReadMember(target, name, diagnosticKey) is T value ? value : fallback;

    /// <summary>
    /// Writes a field the FCS contract must have. A miss here means the FCS build is incompatible
    /// in a way that would otherwise produce a silently wrong fire mission, so this is the one
    /// place the module is allowed to throw at its caller: better a loud failure than a wrong shell.
    /// </summary>
    private void SetRequired(Type taskType, object task, string name, object? value)
    {
        var field = taskType.GetField(name, PublicInstance);
        if (field == null)
        {
            WarnOnce($"ArtilleryTask.{name}", $"required field 'ArtilleryTask.{name}' missing — incompatible FCS build");
            throw new MissingFieldException(taskType.FullName, name);
        }

        field.SetValue(task, value);
    }

    /// <summary>
    /// Writes an optional field: present on the fork, absent on stock FCS. Failure degrades the
    /// task (no priority, no motion, no lifetime) but never blocks the shot.
    /// </summary>
    private void TrySet(Type taskType, object task, string name, object? value, string diagnosticKey)
    {
        try
        {
            var field = taskType.GetField(name, PublicInstance);
            if (field == null)
            {
                WarnOnce(diagnosticKey, $"optional field 'ArtilleryTask.{name}' missing — feature disabled on this FCS build");
                return;
            }

            field.SetValue(task, value);
        }
        catch (Exception ex)
        {
            WarnOnce(diagnosticKey, $"writing 'ArtilleryTask.{name}' threw: {Unwrap(ex).Message}");
        }
    }

    /// <summary>
    /// Finds an instance method by name and exact parameter types, falling back to a unique
    /// by-name match with the same arity so an added optional parameter on the FCS side does not
    /// silently disable a capability. Ambiguity never throws — it just yields no method.
    /// </summary>
    private static MethodInfo? FindMethod(Type type, string name, params Type[] parameterTypes)
    {
        try
        {
            var exact = type.GetMethod(name, AnyInstance, null, parameterTypes, null);
            if (exact != null) return exact;
        }
        catch
        {
            // fall through to the by-name scan
        }

        try
        {
            return type.GetMethods(AnyInstance)
                .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == parameterTypes.Length);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A reflected call blew up inside FCS. The module is not allowed to throw at its callers, and
    /// every caller treats "not ok" as failure, so the exception travels back as a result string.
    /// </summary>
    private string CallFailed(string member, Exception ex)
    {
        var inner = Unwrap(ex);
        WarnOnce($"call:{member}", $"{member} threw: {inner.Message}");
        return $"{member} failed: {inner.Message}";
    }

    private static Exception Unwrap(Exception ex) =>
        (ex as TargetInvocationException)?.InnerException ?? ex;

    /// <summary>
    /// One MelonLogger line per member, for the lifetime of this gateway (§3.4-11), of which the
    /// mod owns exactly one. A renamed FCS member used to degrade a whole feature with zero log
    /// output; now it says so once and goes quiet — failures still travel to callers as return
    /// strings, and this module adds no log spam on top of them.
    /// Deliberately not reset on a Logic hot-swap: re-announcing every miss on each F9 would
    /// defeat the "once" that makes these lines readable. English, like all MelonLog output.
    /// </summary>
    private void WarnOnce(string key, string message)
    {
        if (!_warnedMembers.Add(key)) return;

        try
        {
            MelonLogger.Warning($"[FcsGateway] {message}");
        }
        catch
        {
            // logging must never be the thing that breaks a fire mission
        }
    }
}
