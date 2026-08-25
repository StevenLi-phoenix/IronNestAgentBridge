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
    private readonly HashSet<int> _reportedCorrections = new();

    public void Reset()
    {
        _lastImpactLocal.Clear();
        _reportedCorrections.Clear();
    }

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

        PollCorrectionHints();
    }

    /// <summary>
    /// The game's own miss feedback: each impact spawns an ImpactVisualCorrections that shows
    /// the player a yellow arrow toward the nearest target plus a coarse range text. Both are
    /// deliberately imprecise (tiered direction error, quantized distance), so we relay exactly
    /// the player-visible fidelity: a bearing SECTOR and the displayed range string — never the
    /// underlying target position (that would leak fog-of-war intel the player doesn't have).
    /// </summary>
    private void PollCorrectionHints()
    {
        ImpactVisualCorrections[] hints;
        try { hints = UnityEngine.Object.FindObjectsOfType<ImpactVisualCorrections>(); }
        catch { return; }

        foreach (var hint in hints)
        {
            int key;
            bool evaluated, isHit;
            try
            {
                key = hint.GetInstanceID();
                evaluated = hint._initialEvaluated;
                isHit = hint._isHit;
            }
            catch { continue; }

            if (!evaluated || !_reportedCorrections.Add(key))
                continue;

            Vector2 impactLocal;
            try { impactLocal = hint._impactLocalPos; } catch { _reportedCorrections.Remove(key); continue; }
            var kmX = 10.016f + impactLocal.x * 3.8164f;
            var kmY = 5.235f + impactLocal.y * 3.8164f;
            var at = $"km({kmX:F2},{kmY:F2}) [{Agent.GridMath.GridOf((kmX, kmY))}]";

            if (isHit)
            {
                EventLog.Append("impact_hint", "map", $"弹着确认: {at} 命中(爆炸半径内有目标, 无修正提示)");
                continue;
            }

            // Displayed bearing = true impact→target bearing + the per-target error offset the
            // game rolled for this arrow. Sector half-width comes from the active direction tier;
            // the offset's sign convention doesn't matter — truth stays inside ±error either way.
            float displayedBearing;
            try
            {
                if (hint._currentTarget == null)
                    continue;   // no target resolved — the game shows no arrow either
                var target = hint._currentTargetLocalPos;
                var dx = target.x - impactLocal.x;
                var dy = target.y - impactLocal.y;
                if (dx * dx + dy * dy < 1e-8f)
                    continue;
                displayedBearing = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;
                if (hint._errorOffsetValid)
                    displayedBearing += hint._directionErrorOffsetDeg;
                displayedBearing = (displayedBearing % 360f + 360f) % 360f;
            }
            catch { continue; }

            // Relay only what the player sees: the arrow's rough bearing and the in-game
            // range text verbatim. How imprecise either one is stays undisclosed — the
            // player doesn't know the tier's error parameters, so neither does the agent.
            string range = "";
            try { range = hint.rangeText?.text ?? ""; } catch { }
            var distance = string.IsNullOrWhiteSpace(range) ? "" : $", 距离（不准确）\"{range.Trim()}\"";
            EventLog.Append("impact_hint", "map",
                $"弹着修正提示(黄箭头): 脱靶弹着 {at} → 附近目标在方位约 （不准确）{displayedBearing:F0}° 方向{distance}");
        }
    }
}
