using Il2Cpp;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Reads ACTUAL shell impact points from the game's impact-recon system
/// (ImpactMarkerManager's per-gun impact markers on the tactical map). The systematic
/// offset between intended aim and actual impact is exactly the assumed-origin error,
/// enabling registration-fire recalibration by the agent.
/// </summary>
public class ImpactReader
{
    private readonly Dictionary<int, Vector3> _lastImpactLocal = new();

    public void Reset() => _lastImpactLocal.Clear();

    /// <summary>Poll impact markers; emit an event for each new/moved impact.</summary>
    public void PollAndEmitEvents(Transform? mapSurface)
    {
        if (mapSurface == null)
            return;

        ImpactMarkerManager? manager = null;
        try { manager = ImpactMarkerManager.Instance; } catch { }
        if (manager == null || manager.markerDataList == null)
            return;

        for (var i = 0; i < manager.markerDataList.Count; i++)
        {
            var data = manager.markerDataList[i];
            var instance = data?.activeMarkerInstance;
            if (instance == null || !instance.activeInHierarchy)
                continue;

            var local = mapSurface.InverseTransformPoint(instance.transform.position);
            if (_lastImpactLocal.TryGetValue(i, out var prev)
                && Mathf.Abs(local.x - prev.x) < 0.01f && Mathf.Abs(local.y - prev.y) < 0.01f)
                continue;
            _lastImpactLocal[i] = local;

            var kmX = 10.016f + local.x * 3.8164f;
            var kmY = 5.235f + local.y * 3.8164f;
            var gunName = "";
            try { gunName = data!.gun?.gameObject?.name ?? $"gun{i}"; } catch { gunName = $"gun{i}"; }

            EventLog.Append("shell_impact", "map",
                $"实际弹着({gunName}): km({kmX:F2},{kmY:F2}) [{Agent.GridMath.GridOf((kmX, kmY))}]");
        }
    }
}
