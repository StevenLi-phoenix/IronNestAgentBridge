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
}

public class StateSnapshotDto
{
    public long Timestamp { get; set; }
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
}
