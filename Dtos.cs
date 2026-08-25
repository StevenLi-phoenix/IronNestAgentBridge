namespace IronNestAgentBridge;

// All DTOs use public properties so System.Text.Json serializes them without options tweaks.

public class MapEntityDto
{
    public string Id { get; set; } = "";
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
    public bool Visible { get; set; }
    public string[] ImmuneShells { get; set; } = Array.Empty<string>();
    // Position in tactical-map local space (same space as the draggable markers).
    public float MapX { get; set; }
    public float MapY { get; set; }
    // Firing solution estimate relative to the turret (authoritative solution comes
    // from moving a marker + MapTable.GetMarkTarget; these are for LLM situational awareness).
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
    public string Which { get; set; } = "";   // "primary" = 最高统帅部, "secondary" = 战场报告
    public bool Bound { get; set; }
    public string FullText { get; set; } = ""; // rich tags stripped
}

public class GunDto
{
    public string Side { get; set; } = "";
    public bool Bound { get; set; }
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
    public string? LeftTask { get; set; }
    public string? RightTask { get; set; }
    public bool AutoFireEnabled { get; set; }
    public bool MaxChargeEnabled { get; set; }
    public List<string> PendingTasks { get; set; } = new();
    public int CompletedTaskCount { get; set; }
    public int SuccessfulTaskCount { get; set; }
    public int FailedTaskCount { get; set; }
    // Structured task refs (unique serial #N -> internal map-marker id) for marker
    // bookkeeping — display strings carry only #N and are never parsed.
    public Dictionary<int, int> SerialToMarker { get; set; } = new();
}

public class StateSnapshotDto
{
    public long Timestamp { get; set; }
    // Mission clock ("mm:ss") when the snapshot was taken — same axis as event stamps.
    public string GameTime { get; set; } = "";
    public bool SceneBound { get; set; }
    public float TurretMapX { get; set; }
    public float TurretMapY { get; set; }
    public bool TurretCalibrated { get; set; }
    public List<MapEntityDto> Entities { get; set; } = new();
    public List<MarkerDto> Markers { get; set; } = new();
    public List<TeleprinterDto> Teleprinters { get; set; } = new();
    public List<GunDto> Guns { get; set; } = new();
    public FcsStatusDto Fcs { get; set; } = new();
    // Shell punchcards physically present on the requisition console — the only buyable types this mission.
    public List<string> AvailableShells { get; set; } = new();
    public List<CardDto> Cards { get; set; } = new();
    // Requisition-point balance at snapshot time — every purchase (shell or card) draws on it.
    public int? RequisitionPoints { get; set; }
    public List<ShellSpecDto> ShellSpecs { get; set; } = new();
    // Shells fired but not yet landed: gone from the FCS queue and the gun slots, yet their
    // targets are already served — re-queuing them double-spends ammunition.
    public List<string> InFlightShells { get; set; } = new();
    // Real measured extent of this mission's map ("km(0.0,0.0)-(20.0,10.5)"); firing
    // outside it is rejected and any theoretical impact out there is wasted.
    public string? MapExtentKm { get; set; }
}

public class ShellSpecDto
{
    public string Id { get; set; } = "";
    public int Damage { get; set; }
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

public class BridgeEvent
{
    public long Seq { get; set; }
    public long Timestamp { get; set; }
    public string Type { get; set; } = "";   // telegraph_message | entity_revealed | entity_moved | entity_damaged | entity_destroyed | fcs_task_update
    public string Source { get; set; } = ""; // primary | secondary | map | fcs
    public string Text { get; set; } = "";
    // In-game 24h world clock ("HH:mm") at append time; empty when no clock runs yet.
    public string GameTime { get; set; } = "";
    public object? Data { get; set; }
}

public class FireMissionRequest
{
    // Either give an entityId (bridge moves a spare marker onto it and uses FCS's own math),
    // or give explicit bearing/distance.
    public string? EntityId { get; set; }
    // Direct aim point: grid "K4 5:0" or "kmX,kmY". Preferred over bearing/distance —
    // the solution derives from the live turret piece at enqueue time.
    public string? TargetPoint { get; set; }
    public float? BearingDeg { get; set; }
    public float? DistanceKm { get; set; }
    public string Shell { get; set; } = "HE";
    public int MarkerId { get; set; } = 4;   // which map marker to commandeer for entity targeting
    // 0-100; >=90 (counter-battery) skips the FCS pairing window and wins gun assignment first.
    public int Priority { get; set; } = 50;
    // Small aim-point nudge in km applied after the target resolves (any of the three paths).
    // Exists so the LLM can shift the burst away from nearby friendlies while keeping the
    // target designation; capped at ±0.5 km — larger shifts should just aim elsewhere.
    public float? OffsetKmX { get; set; }
    public float? OffsetKmY { get; set; }
    // A friendly inside the shell's blast radius blocks the mission with a warning;
    // this overrides the block after the LLM has seen and accepted the warning.
    public bool AllowDangerouslyFriendlyFire { get; set; }
    // Linear motion model for a moving target the map can't see (telegraph intel):
    // observed at MotionFrom at world-clock time MotionAtTime ("HH:mm" 24h, default now), moving on
    // MotionBearingDeg at MotionSpeedKmh. FCS extrapolates p(t)=p0+v(t-t0) to impact time.
    public string? MotionFrom { get; set; }
    public float? MotionBearingDeg { get; set; }
    public float? MotionSpeedKmh { get; set; }
    public string? MotionAtTime { get; set; }
}

// LLM-initiated last-minute re-aim of an already-queued/in-preparation FCS task, addressed
// by its unique serial (#N — targetId is the recycled marker id and repeats, never use it).
// FCS never waits for this: with no adjustment the task fires on its original solution; with
// one, the staged re-solve pipeline (pre-aim / pre-fire / manual-wait) lays the new point.
public class AdjustFireRequest
{
    public int Serial { get; set; }
    public string? EntityId { get; set; }
    public string? TargetPoint { get; set; }   // grid "K4 5:0" or "kmX,kmY"
    public float? OffsetKmX { get; set; }      // same semantics/cap as FireMissionRequest
    public float? OffsetKmY { get; set; }
    public bool AllowDangerouslyFriendlyFire { get; set; }
}
