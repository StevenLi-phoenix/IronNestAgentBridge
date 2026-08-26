using IronNestAgentBridge.Agent;
using IronNestAgentBridge.Fcs;
using IronNestAgentBridge.Fire;
using IronNestAgentBridge.GameState;

namespace IronNestAgentBridge.Snapshot;

/// <summary>
/// Assembles the one state view of the battlefield. <c>GET /state</c> and the agent's per-round
/// context are the same object, so a field added for one is immediately visible to the other and
/// a field dropped here disappears from both.
///
/// Three rules govern what is allowed in:
/// <list type="bullet">
/// <item><b>Nothing in the fog.</b> Only entities the player can actually see may be collected;
/// an invisible entity reaching the model is map hacking, not a snapshot.</item>
/// <item><b>The turret's coordinates are never printed to the model.</b> They travel in this DTO
/// because the HTTP client and the <c>get_assumed_turret_position</c> tool need them, but the
/// snapshot TEXT the agent reads must never contain them: the agent's belief about where its own
/// gun stands may only come from High Command dispatches and its own registration fire. Whoever
/// renders this DTO into text owns that rule.</item>
/// <item><b>Only what this mission stocks.</b> Shell specifications are filtered down to the cards
/// actually on the requisition console, so the model never plans around ammunition it cannot buy.</item>
/// </list>
///
/// Main thread only.
/// </summary>
public sealed class SnapshotBuilder
{
    private readonly MapReader _map;
    private readonly FcsGateway _fcs;
    private readonly ShellTracker _shells;
    private readonly TeleprinterReader _teleprinters;

    public SnapshotBuilder(MapReader map, FcsGateway fcs, ShellTracker shells, TeleprinterReader teleprinters)
    {
        _map = map;
        _fcs = fcs;
        _shells = shells;
        _teleprinters = teleprinters;
    }

    /// <summary>
    /// Reads the whole world once.
    /// </summary>
    /// <param name="turretCalibrated">
    /// The mod's calibration flag. Deliberately a behaviour flag, not a property of the piece's
    /// position: it is true only once somebody — the agent's tool or a hand the manual-calibration
    /// detector caught — has actually placed the piece this mission.
    /// </param>
    public StateSnapshotDto Build(bool turretCalibrated)
    {
        var snapshot = new StateSnapshotDto
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            GameTime = EventLog.GameClock,

            // Lets a consumer tell whether this snapshot is older or newer than the events it has
            // already read, instead of guessing from wall-clock timestamps.
            LatestSeq = EventLog.LatestSeq,

            SceneBound = _map.IsBound,
            Teleprinters = _teleprinters.ReadAll(),
            Guns = GunStateReader.ReadBoth(),
            Fcs = _fcs.ReadStatus(),
            Cards = AmmoReader.ReadCards(),
            RequisitionPoints = AmmoReader.ReadRequisitionPoints(),
            InFlightShells = _shells.DescribeInFlight(),
        };

        foreach (var card in snapshot.Cards) snapshot.AvailableShells.Add(card.Id);

        snapshot.ShellSpecs = StockedSpecs(snapshot.AvailableShells);

        AnnotateFcsTasks(snapshot.Fcs);
        ReadMissionIdentity(snapshot);

        // Coordinates are meaningless until the map is bound, and reporting stale ones would be
        // worse than reporting none.
        if (_map.IsBound)
        {
            snapshot.MapExtentKm = GridMath.MapBoundsText;

            var turret = _map.TurretLocalOnMap();
            // map-local units, NOT km: this is the frame the draggable pieces live in.
            snapshot.TurretMapX = turret.x;
            snapshot.TurretMapY = turret.y;
            snapshot.TurretCalibrated = turretCalibrated;

            snapshot.Entities = _map.ReadEntities();
            snapshot.Markers = _map.ReadMarkers();
        }

        return snapshot;
    }

    /// <summary>
    /// Attaches the bridge's own target labels to the FCS task lines, so a queue entry reads as
    /// the mission the agent asked for rather than as bare gunnery data.
    /// </summary>
    private void AnnotateFcsTasks(FcsStatusDto fcs)
    {
        fcs.LeftTask = _shells.AnnotateTask(fcs.LeftTask);
        fcs.RightTask = _shells.AnnotateTask(fcs.RightTask);

        for (var i = 0; i < fcs.PendingTasks.Count; i++)
        {
            fcs.PendingTasks[i] = _shells.AnnotateTask(fcs.PendingTasks[i]) ?? fcs.PendingTasks[i];
        }
    }

    /// <summary>
    /// Shell specifications narrowed to what this console stocks. The specification table is
    /// scanned off loaded assets and therefore lists ammunition from other missions too.
    /// </summary>
    private static List<ShellSpecDto> StockedSpecs(List<string> availableShells)
    {
        var stocked = new HashSet<string>(availableShells, StringComparer.OrdinalIgnoreCase);

        var result = new List<ShellSpecDto>();
        foreach (var spec in AmmoReader.ReadShellSpecs())
        {
            if (stocked.Contains(spec.Id)) result.Add(spec);
        }

        return result;
    }

    /// <summary>
    /// Mission and scene identity. The scene name is diagnostics only and must never be used to
    /// classify a mission — every mission in this game runs in the same scene, so it cannot.
    /// The mission name is the localised display name and doubles as the key of the commander's
    /// mission-intel table, so it is taken verbatim.
    /// </summary>
    private static void ReadMissionIdentity(StateSnapshotDto snapshot)
    {
        try
        {
            snapshot.SceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        }
        catch
        {
            snapshot.SceneName = null;
        }

        try
        {
            var mission = Il2Cpp.MissionManager.Instance?.CurrentMission;
            snapshot.MissionName = mission?.MissionName?.Get() ?? "";
            snapshot.MissionType = mission?.MissionType.ToString() ?? "";
        }
        catch
        {
            snapshot.MissionName = "";
            snapshot.MissionType = "";
        }
    }
}
