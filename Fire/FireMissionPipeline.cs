using IronNestAgentBridge.Agent;
using IronNestAgentBridge.Fcs;
using IronNestAgentBridge.GameState;
using UnityEngine;

namespace IronNestAgentBridge.Fire;

/// <summary>
/// Everything that stands between "the model asked for a shell here" and FCS receiving a task:
/// target resolution, offset clamping, out-of-bounds rejection, range validation, the blast
/// survey, motion-model transcription and finally the enqueue.
///
/// <b>The order of the steps is itself the protocol.</b> Each check exists to refuse a mission
/// before a later, more expensive or more confusing check can misdiagnose it — an out-of-map aim
/// point must be reported as bad coordinates, not as "no friendlies nearby", and a mission that
/// exceeds the gun's range must never reach the safety survey and get a receipt implying it was
/// ever going to fire. Re-ordering these steps changes what the agent is told and how it recovers.
///
/// The bridge never touches a physical map marker: T1–T8 belong to the player, T9/T10 are driven
/// by FCS, and both enqueue and re-aim travel as pure coordinates.
///
/// Main thread only — every method reads live game state through the map reader and FCS.
/// </summary>
public sealed class FireMissionPipeline
{
    /// <summary>Offsets nudge a burst clear of friendlies; they are not an aiming mechanism.</summary>
    public const float MaxOffsetKm = 0.5f;

    /// <summary>
    /// Physical ceiling of the gun: six powder charges at 5 km each. No shell, no charge table and
    /// no target-resolution path may produce a mission beyond it.
    /// </summary>
    public const float GunMaxRangeKm = 30f;

    /// <summary>Used when a shell's charge table cannot be read; the C6 ceiling still applies.</summary>
    public const float FallbackMaxRangeKm = 40f;

    /// <summary>Muzzle-to-impact estimate: 0.4 km/s of flight plus a fixed laying overhead.</summary>
    public const float ShellSpeedKmPerSecond = 0.4f;
    public const float FlightOverheadSeconds = 25f;

    private readonly MapReader _map;
    private readonly FcsGateway _fcs;
    private readonly ShellTracker _shells;

    public FireMissionPipeline(MapReader map, FcsGateway fcs, ShellTracker shells)
    {
        _map = map;
        _fcs = fcs;
        _shells = shells;
    }

    /// <summary>The km-frame origin, the only legal base point for a motion track.</summary>
    private static (float x, float y) MapOriginKm => (MapFrame.MapOffsetX, MapFrame.MapOffsetY);

    // =======================================================================================
    // fire
    // =======================================================================================

    /// <summary>
    /// Resolves, vets and enqueues one fire mission.
    /// </summary>
    /// <returns>
    /// <c>"ok (#N)"</c> plus any survey suffix on success; otherwise a refusal written for the
    /// model, which is expected to act on it rather than retry blindly.
    /// </returns>
    public string QueueFireMission(FireMissionRequest req)
    {
        if (!_map.IsBound) return "tactical map not bound";

        var turretLocal = _map.TurretLocalOnMap();
        var turretKm = MapFrame.LocalToKm(turretLocal);

        // ---- 0. shell id normalisation, once, at the entry ------------------------------------
        // The model is shown the game's card ids (PCLM, SMOKE…) while FCS's bullet enum spells
        // some of them differently (PLCM, SMK). Normalising here — and nowhere else — keeps the
        // range table, the blast survey, the ledger, the receipt and FCS's Enum.Parse all talking
        // about the same shell. Blank is left alone so it still earns the "unknown shell" refusal.
        var shell = string.IsNullOrWhiteSpace(req.Shell) ? req.Shell : AmmoReader.NormalizeShellId(req.Shell);

        // ---- 1. target resolution: entityId beats targetPoint beats bearing+distance ---------
        Vector3 aimLocal;
        string label;

        // Whether the aim point was computed FROM the assumed turret position. It decides which
        // diagnosis an out-of-bounds aim point gets, and the distinction matters: on an absolute
        // coordinate the turret is not in the maths at all, so telling the model to go doubt its
        // calibration would send it chasing the wrong fault.
        bool derivedFromTurret;

        if (!string.IsNullOrWhiteSpace(req.EntityId))
        {
            var entity = _map.FindEntity(req.EntityId!);
            if (entity == null)
                return $"entity '{req.EntityId}' not visible on the command table (fog of war or bad id)";

            aimLocal = new Vector3(entity.MapX, entity.MapY, 0f);
            label = req.EntityId!;
            derivedFromTurret = false;
        }
        else if (!string.IsNullOrWhiteSpace(req.EffectiveTargetPoint))
        {
            var spec = req.EffectiveTargetPoint!;
            var km = GridMath.ParsePoint(spec, turretKm);
            if (km == null) return $"cannot parse target '{spec}' (grid like 'K4 5:0' or 'kmX,kmY')";

            aimLocal = MapFrame.KmToLocal(km.Value.x, km.Value.y);
            label = spec;
            derivedFromTurret = false;
        }
        else if (req.BearingDeg.HasValue && req.DistanceKm.HasValue)
        {
            aimLocal = _map.SolutionToMapLocal(req.BearingDeg.Value, req.DistanceKm.Value);
            label = $"bearing {req.BearingDeg.Value:F1}°, {req.DistanceKm.Value:F2} km";
            derivedFromTurret = true;
        }
        else
        {
            return "need entityId, target, or bearingDeg+distanceKm";
        }

        // ---- 2. offset clamp ----------------------------------------------------------------
        var offsetError = ApplyOffset(req.OffsetKmX, req.OffsetKmY, ref aimLocal, ref label);
        if (offsetError != null) return offsetError;

        // ---- 3. out-of-bounds, defence in depth ----------------------------------------------
        var aimKm = MapFrame.LocalToKm(aimLocal);
        if (!GridMath.InMapBounds(aimKm))
        {
            return derivedFromTurret
                ? $"aim point km({aimKm.x:F1},{aimKm.y:F1}) is outside the map — rejected. " +
                  "This aim derives from the ASSUMED turret position + bearing/distance: either the params are wrong, " +
                  "or the assumed turret position is off/OOB — check get_assumed_turret_position and recalibrate if unreliable"
                : $"target coordinates km({aimKm.x:F1},{aimKm.y:F1}) are outside the map — rejected. " +
                  "Bad fire params (grid/km parse or triangulation error); the turret position is irrelevant to this path";
        }

        // ---- 4. range, on every path ---------------------------------------------------------
        // Not just when the request spelled out a distance: a grid reference, an entity and a
        // triangulated point can all land beyond the gun just as easily as a bad distance can.
        var delta = aimLocal - turretLocal;
        delta.z = 0f;
        var bearingDeg = MapFrame.BearingOf(delta);
        var distanceKm = MapFrame.DistanceKm(delta);

        var specs = AmmoReader.ReadShellSpecs();
        var maxRange = MaxRangeKm(shell, specs);
        if (distanceKm > maxRange)
            return $"distance {distanceKm:F1}km exceeds {shell} max range {maxRange:F1}km — rejected";

        // ---- 5. no budget gate on fire, deliberately ------------------------------------------
        // Some missions start at zero requisition points with shells already in the chamber, and
        // firing what is loaded costs nothing; points also regenerate over time. Any "can we
        // afford this?" guess made here refuses missions that would have worked. The agent sees
        // the live balance in every snapshot, and a purchase it truly cannot fund fails inside FCS.

        // ---- 6. safety layer -------------------------------------------------------------------
        var entities = _map.ReadEntities();
        var suffix = BlastSurvey.SurveyBlast(
            shell, aimKm.x, aimKm.y, req.AllowDangerouslyFriendlyFire,
            entities, specs, out var rejection, out var hostilesInRadius);
        if (rejection != null) return rejection;

        // ---- 7. motion model -------------------------------------------------------------------
        FcsGateway.MotionSpec? motion = null;
        if (!string.IsNullOrWhiteSpace(req.MotionFrom))
        {
            var motionError = TryBuildMotion(req, out motion);
            if (motionError != null) return motionError;
        }

        // ---- 8. blind-fire warning (a warning, never a refusal) ---------------------------------
        // Pre-planned interdiction and predicted fire are legitimate and must go through; using a
        // killing shell as a reconnaissance probe is what gets called out.
        if (!BlastSurvey.IsHarmless(shell)
            && string.IsNullOrWhiteSpace(req.EntityId)
            && hostilesInRadius == 0
            && motion == null)
        {
            suffix += $"; ⚠盲射警告: {shell}是杀伤弹而弹着半径内无已揭示敌目标——侦察盲射必须用STAR, 校射用DRIL; " +
                      "只有明确的预判/封锁打击才允许杀伤弹盲射, 否则立即cancel_pending_task省下这笔钱";
        }

        // ---- 9. enqueue as a pure aim point -----------------------------------------------------
        var trackEntityId = string.IsNullOrWhiteSpace(req.EntityId) ? null : req.EntityId;
        var result = _fcs.EnqueueAimPoint(
            aimLocal.x, aimLocal.y, bearingDeg, distanceKm, shell, req.Priority,
            out var serial, trackEntityId, motion, req.ValidForSeconds);

        if (!string.Equals(result, "ok", StringComparison.Ordinal)) return result + suffix;

        // FCS said yes but handed back no serial: without one the task cannot be adjusted,
        // cancelled or reconciled, so booking it would create a ledger entry that can never be
        // closed. Report the ambiguity instead of inventing certainty in either direction.
        if (serial <= 0) return "FCS 未返回任务编号(版本不兼容?), 任务状态未知" + suffix;

        _shells.Register(serial, label, shell, aimKm.x, aimKm.y,
            distanceKm / ShellSpeedKmPerSecond + FlightOverheadSeconds);

        EventLog.Append("fcs_task_update", "fcs",
            $"fire mission queued on {label} ({shell}, P{req.Priority}) as #{serial}");

        return $"ok (#{serial}){suffix}";
    }

    // =======================================================================================
    // adjust
    // =======================================================================================

    /// <summary>
    /// Last-moment re-aim of a task that is queued or already loading, addressed by serial only —
    /// marker ids are recycled and would eventually address someone else's task.
    ///
    /// FCS never waits for this: an un-adjusted task fires on its original point, and an adjusted
    /// one is picked up by the staged re-solve pipeline at its next opportunity.
    /// </summary>
    public string AdjustFireMission(AdjustFireRequest req)
    {
        if (!_map.IsBound) return "tactical map not bound";

        var serial = req.EffectiveSerial;
        var turretLocal = _map.TurretLocalOnMap();
        var turretKm = MapFrame.LocalToKm(turretLocal);

        // No bearing/distance path here: a re-aim is a correction to a known point, and deriving
        // the new point from the assumed turret position would fold a calibration error into it.
        Vector3 aimLocal;
        string label;

        if (!string.IsNullOrWhiteSpace(req.EntityId))
        {
            var entity = _map.FindEntity(req.EntityId!);
            if (entity == null)
                return $"entity '{req.EntityId}' not visible on the command table (fog of war or bad id)";

            aimLocal = new Vector3(entity.MapX, entity.MapY, 0f);
            label = req.EntityId!;
        }
        else if (!string.IsNullOrWhiteSpace(req.EffectiveTargetPoint))
        {
            var spec = req.EffectiveTargetPoint!;
            var km = GridMath.ParsePoint(spec, turretKm);
            if (km == null) return $"cannot parse target '{spec}' (grid like 'K4 5:0' or 'kmX,kmY')";

            aimLocal = MapFrame.KmToLocal(km.Value.x, km.Value.y);
            label = spec;
        }
        else
        {
            return "need target or entityId";
        }

        var offsetError = ApplyOffset(req.OffsetKmX, req.OffsetKmY, ref aimLocal, ref label);
        if (offsetError != null) return offsetError;

        var aimKm = MapFrame.LocalToKm(aimLocal);
        if (!GridMath.InMapBounds(aimKm))
            return $"new aim point km({aimKm.x:F1},{aimKm.y:F1}) is outside the map — rejected";

        // The shell is a property of the task, not of the request: look it up structurally.
        // A true return with a null shell is legal, and then there is no radius to survey and no
        // charge table to validate against.
        _fcs.TryGetTaskInfo(serial, out var shell, out _);

        var specs = AmmoReader.ReadShellSpecs();

        if (shell != null)
        {
            var delta = aimLocal - turretLocal;
            delta.z = 0f;
            var distanceKm = MapFrame.DistanceKm(delta);
            var maxRange = MaxRangeKm(shell, specs);
            if (distanceKm > maxRange)
                return $"distance {distanceKm:F1}km exceeds {shell} max range {maxRange:F1}km — rejected";
        }

        var suffix = BlastSurvey.SurveyBlast(
            shell, aimKm.x, aimKm.y, req.AllowDangerouslyFriendlyFire,
            _map.ReadEntities(), specs, out var rejection, out _);
        if (rejection != null) return rejection;

        var result = _fcs.AdjustTaskAim(serial, aimLocal.x, aimLocal.y);

        if (result.StartsWith("ok", StringComparison.Ordinal))
        {
            // Keep the impact-matching point fresh, or the shell will land on the new aim point
            // and be matched against the old one.
            _shells.UpdateAim(serial, label, aimKm.x, aimKm.y);
            EventLog.Append("fcs_task_update", "fcs", $"#{serial} 瞄准点已调整 → {label}");
        }

        // Unlike a refused enqueue, a refused re-aim keeps the suffix: the survey it carries is
        // usually the reason the model wanted to re-aim in the first place.
        return result + suffix;
    }

    // =======================================================================================
    // shared steps
    // =======================================================================================

    /// <summary>
    /// Validates and applies the impact nudge, in place. Returns a refusal string, or null when
    /// the offset was accepted (including the common case of no offset at all).
    /// </summary>
    private static string? ApplyOffset(float? offsetKmX, float? offsetKmY, ref Vector3 aimLocal, ref string label)
    {
        var offX = offsetKmX ?? 0f;
        var offY = offsetKmY ?? 0f;

        if (MathF.Abs(offX) > MaxOffsetKm || MathF.Abs(offY) > MaxOffsetKm)
        {
            return "offset exceeds ±0.5km — offsets are for nudging the burst clear of friendlies; " +
                   "aim at different coordinates instead";
        }

        if (offX == 0f && offY == 0f) return null;

        aimLocal = new Vector3(
            aimLocal.x + offX / MapFrame.MapLocalToKm,
            aimLocal.y + offY / MapFrame.MapLocalToKm,
            aimLocal.z);

        // Explicit signs: the model has to be able to read the direction of its own nudge back.
        label += $" 偏移({offX:+0.00;-0.00},{offY:+0.00;-0.00})km";
        return null;
    }

    /// <summary>
    /// Longest range the given shell can reach, capped at the gun's own C6 ceiling. An unreadable
    /// charge table falls back to a generous figure, but the ceiling still applies — the gun
    /// cannot throw a shell past 30 km whatever the table says.
    /// </summary>
    private static float MaxRangeKm(string? shell, IReadOnlyList<ShellSpecDto> specs)
    {
        var tableMax = 0f;

        foreach (var spec in specs)
        {
            if (!string.Equals(spec.Id, shell, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (var range in spec.ChargeRanges)
            {
                if (range.MaxKm > tableMax) tableMax = range.MaxKm;
            }

            break;
        }

        if (tableMax <= 0f) tableMax = FallbackMaxRangeKm;
        return MathF.Min(tableMax, GunMaxRangeKm);
    }

    /// <summary>
    /// Transcribes the LLM's description of a moving target into the linear model FCS extrapolates
    /// from. The model is never allowed to compute lead itself — it supplies an observation
    /// (where, which way, how fast, when) and FCS does the gunnery.
    /// </summary>
    /// <returns>A refusal string, or null when <paramref name="motion"/> was built.</returns>
    private static string? TryBuildMotion(FireMissionRequest req, out FcsGateway.MotionSpec? motion)
    {
        motion = null;

        var spec = req.MotionFrom!.Trim();

        // Absolute positions only. A relative reference resolves against the assumed turret
        // position, which would silently re-anchor the whole track on a calibration that the
        // observation itself has nothing to do with.
        if (string.Equals(spec, "turret", StringComparison.OrdinalIgnoreCase))
            return "运动点必须用绝对网格或 km 坐标";

        var km = GridMath.ParsePoint(spec, MapOriginKm);
        if (km == null) return $"cannot parse motionFrom '{req.MotionFrom}'";

        if (!req.MotionBearingDeg.HasValue || !req.MotionSpeedKmh.HasValue)
            return "motionFrom requires motionBearingDeg and motionSpeedKmh";

        var t0 = AgentBridgeMod.MissionClockSeconds;

        if (!string.IsNullOrWhiteSpace(req.MotionAtTime))
        {
            // An absolute timestamp is only meaningful against the in-game 24h world clock. On a
            // mission that only has a stopwatch there is no shared axis to anchor it to, and
            // guessing one would place the target somewhere it has never been.
            if (!AgentBridgeMod.WorldClockAvailable)
                return "本关无世界钟, 请改用相对描述或省略 atTime";

            if (!TryParseWorldClock(req.MotionAtTime!, out t0))
                return $"cannot parse motionAtTime '{req.MotionAtTime}' (expect 24h \"HH:mm\", same clock as event stamps)";
        }

        var rad = req.MotionBearingDeg.Value * Mathf.Deg2Rad;
        var speedLocalPerSecond = req.MotionSpeedKmh.Value / 3600f / MapFrame.MapLocalToKm;
        var origin = MapFrame.KmToLocal(km.Value.x, km.Value.y);

        // Bearing convention: 0 = map north, clockwise, so the unit vector is (sin, cos).
        motion = new FcsGateway.MotionSpec(
            origin.x,
            origin.y,
            Mathf.Sin(rad) * speedLocalPerSecond,
            Mathf.Cos(rad) * speedLocalPerSecond,
            t0);

        return null;
    }

    /// <summary>Parses "HH:mm" or "HH:mm:ss" into seconds since midnight on the world clock.</summary>
    private static bool TryParseWorldClock(string text, out float seconds)
    {
        seconds = 0f;

        var parts = text.Trim().Split(':');
        if (parts.Length is not (2 or 3)) return false;

        if (!int.TryParse(parts[0], out var hours)) return false;
        if (!int.TryParse(parts[1], out var minutes)) return false;

        var secs = 0;
        if (parts.Length == 3 && !int.TryParse(parts[2], out secs)) return false;

        seconds = hours * 3600 + minutes * 60 + secs;
        return true;
    }
}
