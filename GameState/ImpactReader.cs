using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Real impact points and the game's own miss feedback.
///
/// The systematic gap between where we aimed and where the shell actually landed IS the error in
/// our assumed turret position — it is the only evidence registration fire produces, so nothing
/// here may be smoothed, averaged or suppressed.
///
/// Stateful; <see cref="Reset"/> clears everything for F9 / a new mission.
/// </summary>
public sealed class ImpactReader
{
    /// <summary>map-local movement below this is the same impact re-read, not a new one.</summary>
    private const float ImpactEpsilonLocal = 0.01f;

    /// <summary>
    /// How often destroyed objects are swept out of the state tables (correction hints and impact
    /// history alike). Deliberately slow: a marker or hint that is merely deactivated for a moment
    /// must not lose its record and come back as a fresh broadcast.
    /// </summary>
    private const float HintSweepSeconds = 30f;

    /// <summary>
    /// Last impact position per marker INSTANCE, not per marker-list index. Indices are recycled
    /// between guns; instance ids are not, and a stale index once made one gun's impact look like
    /// the other's.
    /// </summary>
    private readonly Dictionary<int, Vector3> _lastImpactLocal = new();

    /// <summary>Impact keys seen missing at the previous sweep; dropped if missing again.</summary>
    private readonly HashSet<int> _impactPruneCandidates = new();

    /// <summary>Correction hints already broadcast, so each yellow arrow is narrated once.</summary>
    private readonly HashSet<int> _reportedCorrections = new();

    private float _nextImpactSweep;

    private float _nextHintSweep;

    /// <summary>Clears impact history and the reported-hint set.</summary>
    public void Reset()
    {
        _lastImpactLocal.Clear();
        _impactPruneCandidates.Clear();
        _reportedCorrections.Clear();
        _nextImpactSweep = 0f;
        _nextHintSweep = 0f;
    }

    /// <summary>
    /// Polls the impact markers and then the correction hints.
    /// </summary>
    /// <param name="mapSurface">From <see cref="MapReader.MapSurface"/>; null means unbound.</param>
    /// <param name="resolveImpact">
    /// Given an impact in km, settles the nearest in-flight shell and returns its identity string
    /// (e.g. <c>#12 K4 5:0 (HE)</c>), or null when nothing matches. Supplied by the shell tracker.
    /// </param>
    public void PollAndEmitEvents(Transform? mapSurface, Func<float, float, string?>? resolveImpact)
    {
        if (mapSurface == null) return;

        PollImpacts(mapSurface, resolveImpact);
        PollCorrectionHints(mapSurface);
    }

    // ---------------------------------------------------------------- real impacts

    private void PollImpacts(Transform mapSurface, Func<float, float, string?>? resolveImpact)
    {
        ImpactMarkerManager? manager;
        try { manager = ImpactMarkerManager.Instance; }
        catch { return; }
        if (manager == null) return;

        var markers = Il2CppSafe.GetRef(() => manager.markerDataList);
        if (markers == null) return;

        var count = Il2CppSafe.Get(() => markers.Count, 0);
        var live = new HashSet<int>();

        for (var i = 0; i < count; i++)
        {
            var index = i;
            var data = Il2CppSafe.GetRef(() => markers[index]);
            if (data == null) continue;

            var instance = Il2CppSafe.GetRef(() => data.activeMarkerInstance);
            if (instance == null) continue;
            if (!Il2CppSafe.Get(() => instance.activeInHierarchy, false)) continue;

            var instanceId = Il2CppSafe.Get(() => instance.GetInstanceID(), 0);
            if (instanceId == 0) continue;
            live.Add(instanceId);

            // A repeat shot at the same aim point re-creates or re-binds the marker WITHOUT
            // moving it, so a fresh instance counts as a new impact all on its own.
            var instanceChanged = !_lastImpactLocal.TryGetValue(instanceId, out var previous);

            var local = Il2CppSafe.Get(() => mapSurface.InverseTransformPoint(instance.transform.position),
                Vector3.zero);

            var moved = Math.Abs(local.x - previous.x) >= ImpactEpsilonLocal
                     || Math.Abs(local.y - previous.y) >= ImpactEpsilonLocal;

            _lastImpactLocal[instanceId] = local;
            if (!instanceChanged && !moved) continue;

            var km = MapFrame.LocalToKm(local);
            var gunName = Il2CppSafe.Get(() => data.gun.gameObject.name, $"gun{index}");

            string? settled = null;
            if (resolveImpact != null)
            {
                try { settled = resolveImpact(km.x, km.y); }
                catch { settled = null; }
            }

            var grid = Agent.GridMath.GridOf(km);
            var text = $"实际弹着({gunName}): km({km.x:F2},{km.y:F2}) [{grid}]";
            if (settled != null) text += $" → 在途任务 {settled} 已落地销账";

            EventLog.Append("shell_impact", "map", text);
        }

        SweepImpactHistory(live);
    }

    /// <summary>
    /// Bounds the impact table without ever dropping a live marker's de-duplication record.
    ///
    /// Pruning "everything missing from this poll's live set" would be wrong: a single unreadable
    /// poll (an Il2Cpp read that throws, a marker deactivated for an instant, an instance id that
    /// reads back 0) drops the record, and the next poll then sees <c>instanceChanged</c> and
    /// re-broadcasts the SAME impact — which also calls <c>resolveImpact</c> again and settles a
    /// second in-flight shell that never landed. So a key must be absent from two consecutive
    /// sweeps, 30 s apart, before it is dropped; transient dropouts can never survive that.
    /// </summary>
    private void SweepImpactHistory(HashSet<int> live)
    {
        var now = Il2CppSafe.Get(() => Time.realtimeSinceStartup, 0f);
        if (now < _nextImpactSweep) return;
        _nextImpactSweep = now + HintSweepSeconds;

        foreach (var key in _impactPruneCandidates)
        {
            if (!live.Contains(key)) _lastImpactLocal.Remove(key);
        }

        _impactPruneCandidates.Clear();
        foreach (var key in _lastImpactLocal.Keys)
        {
            if (!live.Contains(key)) _impactPruneCandidates.Add(key);
        }
    }

    // ---------------------------------------------------------------- correction hints

    /// <summary>
    /// Retells the game's own miss feedback: a yellow arrow towards the nearest target plus a
    /// coarse distance caption. Both are deliberately imprecise on the game's side.
    ///
    /// Secrecy invariant: only the fidelity the player can see on screen may be repeated. The
    /// target's real position, the error tier and the error offset are all knowledge the player
    /// does not have — leaking any of them is map hacking.
    /// </summary>
    private void PollCorrectionHints(Transform mapSurface)
    {
        Il2CppArrayBase<ImpactVisualCorrections>? found;
        try { found = UnityEngine.Object.FindObjectsOfType<ImpactVisualCorrections>(); }
        catch { return; }
        if (found == null) return;

        Il2CppArrayBase<ImpactVisualCorrections> hints = found;
        var count = Il2CppSafe.Get(() => hints.Length, 0);
        var live = new HashSet<int>();

        for (var i = 0; i < count; i++)
        {
            var index = i;
            var hint = Il2CppSafe.GetRef(() => hints[index]);
            if (hint == null) continue;

            int key;
            bool evaluated;
            bool isHit;
            try
            {
                key = hint.GetInstanceID();
                evaluated = hint._initialEvaluated;
                isHit = hint._isHit;
            }
            catch
            {
                continue;
            }

            live.Add(key);

            // The game has not finished judging this impact yet.
            if (!evaluated) continue;

            // One broadcast per hint, ever.
            if (!_reportedCorrections.Add(key)) continue;

            Vector2 impactLocal;
            try
            {
                impactLocal = hint._impactLocalPos;
            }
            catch
            {
                // Put the key back so a later poll can retry this hint.
                _reportedCorrections.Remove(key);
                continue;
            }

            var km = MapFrame.LocalToKm(impactLocal.x, impactLocal.y);
            var at = $"km({km.x:F2},{km.y:F2}) [{Agent.GridMath.GridOf(km)}]";

            if (isHit)
            {
                EventLog.Append("impact_hint", "map",
                    $"弹着确认: {at} 命中(爆炸半径内有目标, 无修正提示)");
                continue;
            }

            var target = Il2CppSafe.GetRef(() => hint._currentTarget);
            // No arrow is drawn either, so there is nothing to retell.
            if (target == null) continue;

            Vector2 targetLocal;
            try { targetLocal = hint._currentTargetLocalPos; }
            catch { continue; }

            var dx = targetLocal.x - impactLocal.x;
            var dy = targetLocal.y - impactLocal.y;
            if (dx * dx + dy * dy < 1e-8f) continue;

            var bearing = Mathf.Atan2(dx, dy) * Mathf.Rad2Deg;
            // The same random offset the arrow itself is drawn with. Its sign convention does not
            // matter: the truth lies inside the error band either way.
            Il2CppSafe.Do(() =>
            {
                if (hint._errorOffsetValid) bearing += hint._directionErrorOffsetDeg;
            });
            bearing = MapFrame.NormalizeBearing(bearing);

            var range = Il2CppSafe.GetRef(() => hint.rangeText?.text) ?? "";
            var distance = string.IsNullOrWhiteSpace(range) ? "" : $", 距离（不准确）\"{range.Trim()}\"";

            EventLog.Append("impact_hint", "map",
                $"弹着修正提示(黄箭头): 脱靶弹着 {at} → 附近目标在方位约 （不准确）{bearing:F0}° 方向{distance}");
        }

        SweepReportedCorrections(live);
    }

    /// <summary>
    /// Drops reported-hint keys whose objects are gone. Done on a timer rather than every poll:
    /// a hint that is merely deactivated for a moment must not come back as a fresh broadcast.
    /// </summary>
    private void SweepReportedCorrections(HashSet<int> live)
    {
        var now = Il2CppSafe.Get(() => Time.realtimeSinceStartup, 0f);
        if (now < _nextHintSweep) return;
        _nextHintSweep = now + HintSweepSeconds;

        if (_reportedCorrections.Count == 0) return;

        var stale = new List<int>();
        foreach (var key in _reportedCorrections)
        {
            if (!live.Contains(key)) stale.Add(key);
        }
        foreach (var key in stale) _reportedCorrections.Remove(key);
    }
}
