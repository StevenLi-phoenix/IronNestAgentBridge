using System.Text.Json.Serialization;

namespace IronNestAgentBridge;

// These types are simultaneously the HTTP wire contract (serialized camelCase, nulls omitted)
// and the input of the agent's snapshot renderer. Renaming a member changes two protocols at
// once. Units are fixed throughout: mapX/mapY are tactical-map local units, everything named
// *Km is kilometres, bearings are degrees with 0 = map north increasing clockwise.

/// <summary>The single state view: <c>GET /state</c> and the agent snapshot share it.</summary>
public class StateSnapshotDto
{
    public long Timestamp { get; set; }

    /// <summary>Mission clock at snapshot time; same axis as event timestamps.</summary>
    public string GameTime { get; set; } = "";

    /// <summary>
    /// Newest event sequence at snapshot time, so a client can tell whether the snapshot is
    /// older or newer than the events it has already consumed.
    /// </summary>
    public long LatestSeq { get; set; }

    /// <summary>Coordinates below are meaningless until this is true.</summary>
    public bool SceneBound { get; set; }

    public float TurretMapX { get; set; }
    public float TurretMapY { get; set; }
    public bool TurretCalibrated { get; set; }

    public List<MapEntityDto> Entities { get; set; } = new();
    public List<MarkerDto> Markers { get; set; } = new();
    public List<TeleprinterDto> Teleprinters { get; set; } = new();
    public List<GunDto> Guns { get; set; } = new();
    public FcsStatusDto Fcs { get; set; } = new();
    public List<string> AvailableShells { get; set; } = new();
    public List<CardDto> Cards { get; set; } = new();

    /// <summary>Null means "could not read", which is not the same as a zero balance.</summary>
    public int? RequisitionPoints { get; set; }

    /// <summary>Diagnostics only — every mission runs in the same scene, so it cannot classify one.</summary>
    public string? SceneName { get; set; }

    public string? MissionName { get; set; }
    public string? MissionType { get; set; }
    public List<ShellSpecDto> ShellSpecs { get; set; } = new();
    public List<string> InFlightShells { get; set; } = new();
    public string? MapExtentKm { get; set; }
}

public class MapEntityDto
{
    /// <summary>Addressing key handed to the LLM; must be echoed back verbatim.</summary>
    public string Id { get; set; } = "";

    /// <summary>Raw asset id — carries the civilian / hospital markers.</summary>
    public string RawId { get; set; } = "";

    public string Role { get; set; } = "";
    public int RoleValue { get; set; }
    public string State { get; set; } = "";
    public int StateValue { get; set; }
    public int Health { get; set; }
    public int MaxHealth { get; set; }
    public int Armour { get; set; }
    public int Stars { get; set; }
    public bool IsAlive { get; set; }

    /// <summary>Fog of war. An invisible entity must never reach the LLM.</summary>
    public bool Visible { get; set; }

    public string[] ImmuneShells { get; set; } = Array.Empty<string>();

    /// <summary>Tactical-map local space, the frame the draggable markers live in — not km.</summary>
    public float MapX { get; set; }
    public float MapY { get; set; }

    /// <summary>Situational estimate relative to the turret piece; authoritative gunnery is FCS's.</summary>
    public float BearingDeg { get; set; }
    public float DistanceKm { get; set; }
}

public class MarkerDto
{
    public int Id { get; set; }
    public float MapX { get; set; }
    public float MapY { get; set; }
    public float BearingDeg { get; set; }
    public float DistanceKm { get; set; }
}

public class TeleprinterDto
{
    /// <summary>Only "primary" (High Command) or "secondary" (battlefield reports).</summary>
    public string Which { get; set; } = "";

    public bool Bound { get; set; }

    /// <summary>Whole roll, rich-text tags already stripped.</summary>
    public string FullText { get; set; } = "";
}

public class GunDto
{
    public string Side { get; set; } = "";
    public bool Bound { get; set; }

    /// <summary>Raw ShellId — not normalised the way AmmoReader's card ids are.</summary>
    public string? ChamberedShell { get; set; }

    public int PowderCharges { get; set; }
    public bool CanFire { get; set; }
    public bool IsReloading { get; set; }
    public float CurrentElevation { get; set; }
}

public class FcsStatusDto
{
    public bool ModPresent { get; set; }
    public bool LogicLoaded { get; set; }
    public bool Bound { get; set; }
    public int PendingCount { get; set; }

    /// <summary>Display string for the left gun's task. Display only — never parse it.</summary>
    public string? LeftTask { get; set; }

    /// <summary>Display string for the right gun's task. Display only — never parse it.</summary>
    public string? RightTask { get; set; }

    public bool AutoFireEnabled { get; set; }
    public bool MaxChargeEnabled { get; set; }
    public List<string> PendingTasks { get; set; } = new();
    public int CompletedTaskCount { get; set; }
    public int SuccessfulTaskCount { get; set; }
    public int FailedTaskCount { get; set; }

    /// <summary>
    /// Task serial (#N) to internal map marker id. The only machine-readable route from a
    /// serial to a marker; a serial that vanishes from these keys has left the barrel.
    /// </summary>
    public Dictionary<int, int> SerialToMarker { get; set; } = new();

    /// <summary>
    /// Serial to outcome, "Finished" or "Failed: &lt;reason&gt;". The sole way to tell a shell
    /// that was fired from a task that failed before firing.
    /// </summary>
    public Dictionary<int, string> RecentOutcomes { get; set; } = new();
}

public class ShellSpecDto
{
    public string Id { get; set; } = "";
    public int Damage { get; set; }

    /// <summary>Kilometres, not metres (HE 0.25, HCHE 0.55, AP 0.15).</summary>
    public float ImpactRadius { get; set; }

    public int ProjectilesPerShell { get; set; }
    public int MaxCharges { get; set; }
    public List<ChargeRangeDto> ChargeRanges { get; set; } = new();
}

public class ChargeRangeDto
{
    public int Charge { get; set; }
    public float MinKm { get; set; }
    public float MaxKm { get; set; }
}

public class CardDto
{
    public string Id { get; set; } = "";
    public int Cost { get; set; }
    public int RemainingUses { get; set; }
    public bool IsRecon { get; set; }
}

/// <summary>One entry of the ring buffer; also the wire shape of <c>GET /events</c>.</summary>
public class BridgeEvent
{
    public long Seq { get; set; }
    public long Timestamp { get; set; }
    public string Type { get; set; } = "";
    public string Source { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>Mission clock at append time; empty string when no clock is running.</summary>
    public string GameTime { get; set; } = "";

    public object? Data { get; set; }
}

/// <summary>
/// <c>POST /fire</c> and the <c>fire</c> tool. Unknown members are tolerated by the
/// deserializer, so retired fields (markerId) simply fall on the floor.
/// </summary>
public class FireMissionRequest
{
    public string? EntityId { get; set; }

    /// <summary>Grid ("K4 5:0") or "kmX,kmY".</summary>
    public string? TargetPoint { get; set; }

    /// <summary>Alias for <see cref="TargetPoint"/>: the tool schema calls this field "target".</summary>
    public string? Target { get; set; }

    public float? BearingDeg { get; set; }
    public float? DistanceKm { get; set; }
    public string Shell { get; set; } = "HE";

    /// <summary>0-100. 90 and above skips the FCS batching window and preempts a gun.</summary>
    public int Priority { get; set; } = 50;

    /// <summary>Queue lifetime in seconds; null or 0 means it never expires.</summary>
    public float? ValidForSeconds { get; set; }

    /// <summary>Impact nudge in km, capped at +/-0.5 by the fire pipeline.</summary>
    public float? OffsetKmX { get; set; }
    public float? OffsetKmY { get; set; }

    /// <summary>Never lifts civilian protection — that rule outranks the commander.</summary>
    public bool AllowDangerouslyFriendlyFire { get; set; }

    public string? MotionFrom { get; set; }
    public float? MotionBearingDeg { get; set; }
    public float? MotionSpeedKmh { get; set; }

    /// <summary>24h "HH:mm" on the world clock; only accepted on maps that have one.</summary>
    public string? MotionAtTime { get; set; }

    /// <summary>Resolved target spec: the canonical field wins, the alias fills in.</summary>
    [JsonIgnore]
    public string? EffectiveTargetPoint =>
        !string.IsNullOrWhiteSpace(TargetPoint) ? TargetPoint : Target;
}

/// <summary>
/// <c>POST /adjust</c> and the <c>adjust_fire</c> tool: last-moment re-aim of a queued or
/// loading task. Addressed by serial only — target ids are recycled and must never address a task.
/// </summary>
public class AdjustFireRequest
{
    /// <summary>Unique task serial #N.</summary>
    public int Serial { get; set; }

    /// <summary>Hallucination-tolerance alias for <see cref="Serial"/>.</summary>
    public int? TargetId { get; set; }

    public string? EntityId { get; set; }

    /// <summary>Grid ("K4 5:0") or "kmX,kmY".</summary>
    public string? TargetPoint { get; set; }

    /// <summary>Alias for <see cref="TargetPoint"/>.</summary>
    public string? Target { get; set; }

    public float? OffsetKmX { get; set; }
    public float? OffsetKmY { get; set; }
    public bool AllowDangerouslyFriendlyFire { get; set; }

    /// <summary>Resolved serial: the canonical field wins, the alias fills in.</summary>
    [JsonIgnore]
    public int EffectiveSerial => Serial > 0 ? Serial : TargetId ?? 0;

    /// <summary>Resolved target spec: the canonical field wins, the alias fills in.</summary>
    [JsonIgnore]
    public string? EffectiveTargetPoint =>
        !string.IsNullOrWhiteSpace(TargetPoint) ? TargetPoint : Target;
}
