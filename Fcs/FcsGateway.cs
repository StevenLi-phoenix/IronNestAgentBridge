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
        dto.LeftTask = DescribeTask(Get<object>("LeftTask"));
        dto.RightTask = DescribeTask(Get<object>("RightTask"));
        return dto;
    }

    private static string? DescribeTask(object? task)
    {
        if (task == null) return null;
        var t = task.GetType();
        object? F(string name) => t.GetField(name, AnyInstance)?.GetValue(task);
        try
        {
            return $"T{F("targetId")} {F("bulletType")} brg {F("angel"):F1} dist {F("distance"):F2}km " +
                   $"chg {F("chargeCount")} [{F("progress")}]" +
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
    public string EnqueueByBearing(float bearingDeg, float distanceKm, string shell, int targetId)
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
    public string EnqueueFromMarker(int markerId, string shell)
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

        fsc.GetType().GetMethod("EnqueueTask", AnyInstance)!.Invoke(fsc, new[] { task });
        return "ok";
    }
}
