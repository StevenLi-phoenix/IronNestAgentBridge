using System.Reflection;
using MelonLoader;
using UnityEngine;

namespace IronNestAgentBridge.Fcs;

/// <summary>
/// Reflection bridge into IronNestFCS Smart. Deliberately loose-coupled: the FCS Logic
/// assembly lives in a collectible AssemblyLoadContext that dies on every F9 / scene load,
/// so nothing here may hold a strong typed reference or cache handles across reloads.
///
/// Chain: FcsHostMod (registered melon "IronNestFCS Smart")
///        -> private field _reloader  (LogicReloader)
///        -> public property Current  (IFcsModule / FcsModule)
///        -> private field _fcs       (FSC)
///        -> public EnqueueTask(ArtilleryTask) / public field MapTable / public state props
/// </summary>
public class FcsGateway
{
    private const string FcsModName = "IronNestFCS Smart";
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private object? _lastModule;   // FcsModule instance — identity changes on every F9
    private object? _fsc;

    /// <summary>Resolve the live FSC instance, re-walking the chain if Logic was reloaded.</summary>
    private object? ResolveFsc(out bool modPresent, out bool logicLoaded)
    {
        modPresent = false;
        logicLoaded = false;

        MelonMod? host = null;
        foreach (var melon in MelonMod.RegisteredMelons)
        {
            if (melon.Info != null && melon.Info.Name == FcsModName)
            {
                host = melon;
                break;
            }
        }
        if (host == null)
            return null;
        modPresent = true;

        var reloader = host.GetType().GetField("_reloader", AnyInstance)?.GetValue(host);
        if (reloader == null)
            return null;

        var module = reloader.GetType().GetProperty("Current", AnyInstance)?.GetValue(reloader);
        if (module == null)
        {
            _lastModule = null;
            _fsc = null;
            return null;
        }
        logicLoaded = true;

        if (!ReferenceEquals(module, _lastModule) || _fsc == null)
        {
            _lastModule = module;
            _fsc = module.GetType().GetField("_fcs", AnyInstance)?.GetValue(module);
        }
        return _fsc;
    }

    public FcsStatusDto ReadStatus()
    {
        var dto = new FcsStatusDto();
        var fsc = ResolveFsc(out var modPresent, out var logicLoaded);
        dto.ModPresent = modPresent;
        dto.LogicLoaded = logicLoaded;
        if (fsc == null)
            return dto;

        var t = fsc.GetType();
        T? Get<T>(string name)
        {
            var p = t.GetProperty(name, AnyInstance);
            if (p == null) return default;
            try { return (T?)p.GetValue(fsc); } catch { return default; }
        }

        dto.Bound = Get<bool>("IsBound");
        dto.PendingCount = Get<int>("PendingCount");
        dto.AutoFireEnabled = Get<bool>("AutoFireEnabled");
        dto.MaxChargeEnabled = Get<bool>("MaxChargeEnabled");
        dto.CompletedTaskCount = Get<int>("CompletedTaskCount");
        dto.SuccessfulTaskCount = Get<int>("SuccessfulTaskCount");
        dto.FailedTaskCount = Get<int>("FailedTaskCount");
        var leftObj = Get<object>("LeftTask");
        var rightObj = Get<object>("RightTask");
        dto.LeftTask = DescribeTask(leftObj);
        dto.RightTask = DescribeTask(rightObj);
        RecordRef(dto, leftObj);
        RecordRef(dto, rightObj);
        try
        {
            if (Get<System.Collections.IEnumerable>("QueueCan") is { } queue)
                foreach (var task in queue)
                {
                    if (DescribeTask(task) is { } desc)
                        dto.PendingTasks.Add(desc);
                    RecordRef(dto, task);
                }
        }
        catch { }
        return dto;
    }

    /// <summary>Structured serial→marker-id map so the bridge never regex-parses display strings.</summary>
    private static void RecordRef(FcsStatusDto dto, object? task)
    {
        if (task == null) return;
        try
        {
            var t = task.GetType();
            var serial = t.GetField("serial", AnyInstance)?.GetValue(task) as int?;
            var markerId = t.GetField("targetId", AnyInstance)?.GetValue(task) as int?;
            if (serial is > 0 && markerId.HasValue)
                dto.SerialToMarker[serial.Value] = markerId.Value;
        }
        catch { }
    }

    private static string? DescribeTask(object? task)
    {
        if (task == null) return null;
        var t = task.GetType();
        object? F(string name) => t.GetField(name, AnyInstance)?.GetValue(task);
        try
        {
            var motion = "";
            try { motion = t.GetMethod("MotionSuffix", AnyInstance)?.Invoke(task, new object[] { true }) as string ?? ""; }
            catch { }
            // Unique serial (#N) is the task handle; the recycled marker id stays internal.
            // Stock FCS (no serial field) falls back to the old T-number lead.
            var serial = F("serial") as int?;
            var head = serial is > 0 ? $"#{serial}" : $"T{F("targetId")}";
            return $"{head} {F("bulletType")} brg {F("angel"):F1} dist {F("distance"):F2}km " +
                   $"chg {F("chargeCount")} [{F("progress")}]{motion}" +
                   (Equals(F("failureReason"), "") ? "" : $" fail: {F("failureReason")}");
        }
        catch
        {
            return task.ToString();
        }
    }

    /// <summary>
    /// Enqueue a fire task by explicit firing solution. Main thread only.
    /// Builds an ArtilleryTask inside the current Logic ALC via reflection.
    /// </summary>
    /// <summary>
    /// FCS's shared Requisition console lock (SharedConsoleCoordinator.Requisition, a
    /// CoroutineLock in the Logic ALC). Re-resolved per call — never cache across F9.
    /// Null when FCS isn't loaded; callers then proceed unguarded.
    /// </summary>
    public object? GetRequisitionLock()
    {
        var fsc = ResolveFsc(out _, out _);
        if (fsc == null) return null;
        var shared = fsc.GetType().GetProperty("SharedResources", AnyInstance)?.GetValue(fsc)
                     ?? fsc.GetType().GetField("SharedResources", AnyInstance)?.GetValue(fsc);
        if (shared == null) return null;
        return shared.GetType().GetProperty("Requisition", AnyInstance)?.GetValue(shared);
    }

    /// <summary>
    /// Submit a punchcard purchase DTO to FCS's console coordinator (patched FCS).
    /// Returns null when the FCS build lacks the API — caller falls back to the legacy
    /// bridge-side physical routine.
    /// </summary>
    public string? RequestCardPurchase(string cardId, float? bearingDeg, int priority = 50, string? startGrid = null)
    {
        var fsc = ResolveFsc(out _, out _);
        if (fsc == null) return null;
        var full = fsc.GetType().GetMethod("RequestConsoleCard", AnyInstance,
            new[] { typeof(string), typeof(float), typeof(bool), typeof(int), typeof(string) });
        if (full != null)
            return full.Invoke(fsc, new object?[] { cardId, bearingDeg ?? 0f, bearingDeg.HasValue, priority, startGrid }) as string;
        var withPriority = fsc.GetType().GetMethod("RequestConsoleCard", AnyInstance,
            new[] { typeof(string), typeof(float), typeof(bool), typeof(int) });
        if (withPriority != null)
            return withPriority.Invoke(fsc, new object[] { cardId, bearingDeg ?? 0f, bearingDeg.HasValue, priority }) as string;
        var legacy = fsc.GetType().GetMethod("RequestConsoleCard", AnyInstance,
            new[] { typeof(string), typeof(float), typeof(bool) });
        return legacy?.Invoke(fsc, new object[] { cardId, bearingDeg ?? 0f, bearingDeg.HasValue }) as string;
    }

    /// <summary>Latest completed console card-request outcome (patched FCS), for polling.</summary>
    public string? ReadConsoleCardResult()
    {
        var fsc = ResolveFsc(out _, out _);
        return fsc?.GetType().GetProperty("ConsoleCardRequestResult", AnyInstance)?.GetValue(fsc) as string;
    }

    /// <summary>
    /// Re-aim an already-queued/in-preparation task at a new map-local point (patched FCS).
    /// Non-blocking: FCS never waits for adjustments; its staged re-solve pipeline picks the
    /// new point up. Returns the FCS result string, or a diagnostic when unavailable.
    /// </summary>
    public string AdjustTaskAim(int serial, float localX, float localY)
    {
        var fsc = ResolveFsc(out var modPresent, out var logicLoaded);
        if (fsc == null)
            return !modPresent ? "IronNestFCS Smart mod not present"
                 : !logicLoaded ? "FCS Logic not loaded (scene not bound yet?)"
                 : "FCS instance unavailable";
        var method = fsc.GetType().GetMethod("AdjustTaskAim", AnyInstance);
        if (method == null)
            return "FCS build lacks AdjustTaskAim";
        return method.Invoke(fsc, new object[] { serial, localX, localY }) as string ?? "adjust failed";
    }

    /// <summary>
    /// Shell type + internal marker id of a queued/executing task by unique serial (#N).
    /// Returns false when no live task carries that serial.
    /// </summary>
    public bool TryGetTaskInfo(int serial, out string? shell, out int markerId)
    {
        shell = null;
        markerId = -1;
        var fsc = ResolveFsc(out _, out _);
        if (fsc == null) return false;
        var t = fsc.GetType();
        (string? shell, int markerId)? InfoOf(object? task)
        {
            if (task == null) return null;
            var tt = task.GetType();
            try
            {
                if (tt.GetField("serial", AnyInstance)?.GetValue(task) is not int s || s != serial)
                    return null;
                return (tt.GetField("bulletType", AnyInstance)?.GetValue(task)?.ToString(),
                        tt.GetField("targetId", AnyInstance)?.GetValue(task) as int? ?? -1);
            }
            catch { return null; }
        }
        try
        {
            var hit = InfoOf(t.GetProperty("LeftTask", AnyInstance)?.GetValue(fsc))
                      ?? InfoOf(t.GetProperty("RightTask", AnyInstance)?.GetValue(fsc));
            if (hit == null && t.GetProperty("QueueCan", AnyInstance)?.GetValue(fsc) is System.Collections.IEnumerable queue)
                foreach (var task in queue)
                    if (InfoOf(task) is { } q)
                    {
                        hit = q;
                        break;
                    }
            if (hit is { } info)
            {
                shell = info.shell;
                markerId = info.markerId;
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>Cancel a pending (not yet executing) FCS task by unique serial (#N). Patched FCS only.</summary>
    public string CancelPending(int serial)
    {
        var fsc = ResolveFsc(out var modPresent, out var logicLoaded);
        if (fsc == null)
            return !modPresent ? "FCS mod not present" : !logicLoaded ? "FCS logic not loaded" : "FCS unavailable";
        var method = fsc.GetType().GetMethod("CancelPendingTask", AnyInstance);
        if (method == null)
            return "FCS build lacks CancelPendingTask";
        var cancelled = method.Invoke(fsc, new object[] { serial }) as string;
        return cancelled == null ? $"no pending task with #{serial}" : $"cancelled: {cancelled}";
    }

    private static void TrySetPriority(object task, int priority)
    {
        // Field exists only on our patched FCS build; stock FCS just ignores priority.
        try { task.GetType().GetField("priority")?.SetValue(task, priority); } catch { }
    }

    public string EnqueueByBearing(float bearingDeg, float distanceKm, string shell, int targetId, int priority = 50)
    {
        var fsc = ResolveFsc(out var modPresent, out var logicLoaded);
        if (fsc == null)
            return !modPresent ? "IronNestFCS Smart mod not present"
                 : !logicLoaded ? "FCS Logic not loaded (scene not bound yet?)"
                 : "FCS instance unavailable";

        var logicAsm = fsc.GetType().Assembly;
        var taskType = logicAsm.GetType("IronNestFCS.Logic.FCS.ArtilleryTask");
        var bulletType = logicAsm.GetType("IronNestFCS.Logic.FCS.BulletType");
        if (taskType == null || bulletType == null)
            return "FCS internal types not found (incompatible FCS version?)";

        object bullet;
        try { bullet = Enum.Parse(bulletType, shell, ignoreCase: true); }
        catch { return $"unknown shell type '{shell}'"; }

        var task = Activator.CreateInstance(taskType)!;
        taskType.GetField("targetId")!.SetValue(task, targetId);
        taskType.GetField("angel")!.SetValue(task, bearingDeg);
        taskType.GetField("distance")!.SetValue(task, distanceKm);
        taskType.GetField("position")!.SetValue(task, Vector3.zero);
        taskType.GetField("bulletType")!.SetValue(task, bullet);
        TrySetPriority(task, priority);

        var enqueue = fsc.GetType().GetMethod("EnqueueTask", AnyInstance);
        if (enqueue == null)
            return "FSC.EnqueueTask not found";
        enqueue.Invoke(fsc, new[] { task });
        return "ok";
    }

    /// <summary>
    /// Enqueue using FCS's own marker math: caller must have already moved marker
    /// `markerId` onto the target; this calls MapTable.GetMarkTarget(markerId) so the
    /// bearing/distance/grid come from the exact same code path as a human click.
    /// </summary>
    /// <summary>Linear motion model handed to the patched FCS (map-local frame, mission-clock seconds).</summary>
    public sealed record MotionSpec(float OriginLocalX, float OriginLocalY, float VelLocalX, float VelLocalY, float T0Seconds);

    private static void TrySetMotion(object task, string? trackEntityId, MotionSpec? motion)
    {
        // Fields exist only on our patched FCS build; stock FCS silently ignores them.
        var t = task.GetType();
        try
        {
            if (!string.IsNullOrEmpty(trackEntityId))
                t.GetField("trackEntityId")?.SetValue(task, trackEntityId);
            if (motion != null)
            {
                t.GetField("hasMotion")?.SetValue(task, true);
                t.GetField("motionOriginLocal")?.SetValue(task, new Vector3(motion.OriginLocalX, motion.OriginLocalY, 0f));
                t.GetField("motionVelLocalPerSec")?.SetValue(task, new Vector3(motion.VelLocalX, motion.VelLocalY, 0f));
                t.GetField("motionT0")?.SetValue(task, motion.T0Seconds);
            }
        }
        catch { }
    }

    public string EnqueueFromMarker(int markerId, string shell, int priority = 50,
        string? trackEntityId = null, MotionSpec? motion = null)
    {
        var fsc = ResolveFsc(out var modPresent, out var logicLoaded);
        if (fsc == null)
            return !modPresent ? "IronNestFCS Smart mod not present"
                 : !logicLoaded ? "FCS Logic not loaded (scene not bound yet?)"
                 : "FCS instance unavailable";

        var mapTable = fsc.GetType().GetField("MapTable", AnyInstance)?.GetValue(fsc);
        if (mapTable == null)
            return "FSC.MapTable unavailable";

        var getMark = mapTable.GetType().GetMethod("GetMarkTarget", AnyInstance);
        if (getMark == null)
            return "MapTable.GetMarkTarget not found";
        var task = getMark.Invoke(mapTable, new object[] { markerId });
        if (task == null)
            return $"marker {markerId} not resolvable on map";

        var taskType = task.GetType();
        var bulletType = taskType.Assembly.GetType("IronNestFCS.Logic.FCS.BulletType")!;
        object bullet;
        try { bullet = Enum.Parse(bulletType, shell, ignoreCase: true); }
        catch { return $"unknown shell type '{shell}'"; }
        taskType.GetField("targetId")!.SetValue(task, markerId);
        taskType.GetField("bulletType")!.SetValue(task, bullet);
        TrySetPriority(task, priority);
        TrySetMotion(task, trackEntityId, motion);

        fsc.GetType().GetMethod("EnqueueTask", AnyInstance)!.Invoke(fsc, new[] { task });
        return "ok";
    }
}
