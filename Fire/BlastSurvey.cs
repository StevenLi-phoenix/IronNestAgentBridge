using IronNestAgentBridge.GameState;

namespace IronNestAgentBridge.Fire;

/// <summary>
/// The single blast-radius survey, and with it the single definition of "civilian" and
/// "friendly" that the whole mod shares. Queue-time vetting (<see cref="FireMissionPipeline"/>)
/// and the standing friendly-intrusion patrol (<see cref="ShellTracker"/>) must agree on who is
/// protected, so both read their predicates from here — two copies of this judgement drifting
/// apart is indistinguishable from a targeting error.
///
/// Two rules outrank everything else in this file:
/// <list type="bullet">
/// <item><b>Civilians are identified by id, never by faction.</b> Missions deliberately tag
/// refugees as <c>role=Enemy</c> to make them look shootable.</item>
/// <item><b>Civilian protection cannot be overridden.</b> <c>allowDangerouslyFriendlyFire</c>
/// buys off the risk friendly troops accept; it has no effect on civilians, whatever the game
/// or High Command labelled them.</item>
/// </list>
///
/// Main thread only in practice: the entity and specification lists come from live game reads.
/// </summary>
public static class BlastSurvey
{
    /// <summary>
    /// Shells that cannot hurt anyone: smoke, illumination, the zero-damage reveal round and the
    /// inert training round. They skip the survey, the patrol and the blind-fire warning entirely.
    /// WP is deliberately absent — until its suppression/fire mechanics are proven harmless it
    /// goes through the full IFF check.
    /// </summary>
    public static readonly IReadOnlyList<string> HarmlessShells = new[] { "SMK", "STAR", "TEAR", "DRIL" };

    /// <summary>Below this a shell has no blast at all and there is nothing to survey. Kilometres.</summary>
    public const float MinBlastKm = 0.001f;

    /// <summary>"Uncomfortably close" ring, as a multiple of the blast radius.</summary>
    public const float NearRingFactor = 1.5f;

    // ---------------------------------------------------------------- shared predicates

    /// <summary>Case-insensitive membership of <see cref="HarmlessShells"/>.</summary>
    public static bool IsHarmless(string? shell)
    {
        if (string.IsNullOrWhiteSpace(shell)) return false;

        foreach (var id in HarmlessShells)
        {
            if (string.Equals(id, shell, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Civilian by identifier only: either name carrying "civil" or "hospital", case-insensitive.
    /// Role is never consulted — see the class remarks.
    /// </summary>
    public static bool IsCivilian(MapEntityDto entity) =>
        Mentions(entity.Id, "civil") || Mentions(entity.Id, "hospital") ||
        Mentions(entity.RawId, "civil") || Mentions(entity.RawId, "hospital");

    /// <summary>
    /// Own troops and attached observers. Civilians are excluded here so the two categories stay
    /// mutually exclusive: they are protected by a stronger rule and must not be reported as
    /// merely "friendly", which the commander is allowed to override.
    /// </summary>
    public static bool IsFriendly(MapEntityDto entity)
    {
        if (IsCivilian(entity)) return false;

        var role = entity.Role;
        return role.Contains("Ally", StringComparison.Ordinal) || role == "Spotter";
    }

    /// <summary>Anyone the guns must not drop a shell on: civilians plus friendlies.</summary>
    public static bool IsProtected(MapEntityDto entity) => IsCivilian(entity) || IsFriendly(entity);

    /// <summary>
    /// Blast radius of a shell in KILOMETRES (HE 0.25, HCHE 0.55, AP 0.15). Never metres — that
    /// confusion once made every radius render as "0m" and left the IFF gate wide open.
    /// Returns 0 for an unknown shell, which reads as "nothing to survey".
    /// </summary>
    public static float BlastRadiusKm(string? shell, IReadOnlyList<ShellSpecDto> specs)
    {
        if (string.IsNullOrWhiteSpace(shell)) return 0f;

        foreach (var spec in specs)
        {
            if (string.Equals(spec.Id, shell, StringComparison.OrdinalIgnoreCase)) return spec.ImpactRadius;
        }

        return 0f;
    }

    /// <summary>Planar distance in km from a map-local entity to a km-frame impact point.</summary>
    public static float DistanceToImpactKm(MapEntityDto entity, float kmX, float kmY)
    {
        var km = MapFrame.LocalToKm(entity.MapX, entity.MapY);
        var dx = km.x - kmX;
        var dy = km.y - kmY;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    // ---------------------------------------------------------------- the survey

    /// <summary>
    /// Surveys everything standing inside (and just outside) the blast radius of one impact point.
    /// One method, complete out-parameters: callers must never have to re-derive a refusal or
    /// re-count the covered targets from the receipt text.
    /// </summary>
    /// <param name="shell">Shell id as the caller will fire it; matched case-insensitively.</param>
    /// <param name="kmX">Impact point, km frame.</param>
    /// <param name="kmY">Impact point, km frame.</param>
    /// <param name="allowDanger">
    /// The commander accepting friendly-fire risk. Never lifts civilian protection.
    /// </param>
    /// <param name="entities">Live entity table; only living entities are considered.</param>
    /// <param name="specs">Shell specifications, for the blast radius.</param>
    /// <param name="rejection">Non-null means refuse the mission and return this string as-is.</param>
    /// <param name="hostilesInRadius">
    /// Number of non-protected targets the burst would cover. Zero on a killing shell with no
    /// designated entity is what makes a mission "blind fire".
    /// </param>
    /// <returns>Suffix to append to the caller's receipt; empty when there is nothing to say.</returns>
    public static string SurveyBlast(
        string? shell,
        float kmX,
        float kmY,
        bool allowDanger,
        IReadOnlyList<MapEntityDto> entities,
        IReadOnlyList<ShellSpecDto> specs,
        out string? rejection,
        out int hostilesInRadius)
    {
        rejection = null;
        hostilesInRadius = 0;
        var suffix = "";

        if (IsHarmless(shell)) return suffix;

        // An unknown shell yields radius 0 and is waved through: the bridge does not invent a
        // blast radius, and FCS will refuse an unknown shell on its own.
        var blastKm = BlastRadiusKm(shell, specs);
        if (blastKm <= MinBlastKm) return suffix;

        var nearRingKm = blastKm * NearRingFactor;

        var civiliansInside = new List<string>();
        var friendliesInside = new List<string>();
        var friendliesNear = new List<string>();
        var hostilesCovered = new List<string>();

        foreach (var entity in entities)
        {
            if (!entity.IsAlive) continue;

            var distanceKm = DistanceToImpactKm(entity, kmX, kmY);

            // Buckets are mutually exclusive and evaluated in this order.
            if (IsCivilian(entity))
            {
                if (distanceKm <= blastKm) civiliansInside.Add($"{entity.Id}(距弹着{distanceKm:F2}km)");
                continue;
            }

            if (IsFriendly(entity))
            {
                if (distanceKm <= blastKm) friendliesInside.Add($"{entity.Id}({entity.Role},距弹着{distanceKm:F2}km)");
                else if (distanceKm <= nearRingKm) friendliesNear.Add($"{entity.Id}({distanceKm:F2}km)");
                continue;
            }

            if (distanceKm <= blastKm) hostilesCovered.Add($"{entity.Id}({distanceKm:F2}km)");
        }

        // Civilian protection: hard, first, and not negotiable.
        if (civiliansInside.Count > 0)
        {
            rejection =
                $"平民保护(不可覆盖) — 已拒绝: {Join(civiliansInside)} 在弹着点km({kmX:F2},{kmY:F2})的{shell}爆炸半径{blastKm * 1000f:F0}m内。" +
                "allowDangerouslyFriendlyFire对平民无效; 换弹着点或换更小半径弹种, 平民不是目标——无论其阵营标注是什么";
            return suffix;
        }

        // Friendly fire: a soft refusal the commander may retry through.
        if (friendliesInside.Count > 0 && !allowDanger)
        {
            rejection =
                $"友军误伤警告 — 已拒绝: {Join(friendliesInside)} 在弹着点km({kmX:F2},{kmY:F2})的{shell}爆炸半径{blastKm * 1000f:F0}m内。" +
                "用offsetKmX/offsetKmY把弹着点向远离友军一侧移出半径(会牺牲部分毁伤), 或换更小爆炸半径的弹种; " +
                "确认接受误伤才用allowDangerouslyFriendlyFire=true重试";
            return suffix;
        }

        if (friendliesInside.Count > 0)
        {
            suffix += $"; 警告: 已确认误伤风险, 友军在爆炸半径内: {Join(friendliesInside)}";
        }
        else if (friendliesNear.Count > 0)
        {
            suffix += $"; 注意: 友军贴近弹着点(≤1.5×爆炸半径): {Join(friendliesNear)}";
        }

        hostilesInRadius = hostilesCovered.Count;
        if (hostilesCovered.Count > 0)
        {
            // Lets the model verify that a merged strike really does cover the cluster it meant.
            suffix += $"; 爆炸半径({blastKm * 1000f:F0}m)可同时覆盖: {Join(hostilesCovered)}";
        }

        return suffix;
    }

    private static string Join(List<string> names) => string.Join(", ", names);

    private static bool Mentions(string? value, string needle) =>
        !string.IsNullOrEmpty(value) && value!.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
