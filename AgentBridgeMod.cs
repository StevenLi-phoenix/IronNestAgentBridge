using Il2Cpp;
using IronNestAgentBridge.Fcs;
using IronNestAgentBridge.GameState;
using IronNestAgentBridge.Http;
using MelonLoader;

[assembly: MelonInfo(typeof(IronNestAgentBridge.AgentBridgeMod), "IronNest Agent Bridge", "0.1.0", "stevenli")]
[assembly: MelonGame()]

namespace IronNestAgentBridge;

public class AgentBridgeMod : MelonMod
{
    private const float BindRetrySeconds = 2f;
    private const float MapPollSeconds = 0.5f;
    private const float TelegraphPollSeconds = 1.0f;

    private readonly MapReader _map = new();
    private readonly TeleprinterReader _telegraph = new();
    private readonly FcsGateway _fcs = new();
    private BridgeServer? _server;

    private float _nextBindAttempt;
    private float _nextMapPoll;
    private float _nextTelegraphPoll;

    public override void OnInitializeMelon()
    {
        _server = new BridgeServer(this);
        try
        {
            _server.Start();
        }
        catch (Exception ex)
        {
            MelonLogger.Error($"[AgentBridge] failed to start HTTP server on port {BridgeServer.Port}: {ex.Message}");
        }
    }

    public override void OnDeinitializeMelon() => _server?.Stop();

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _map.Unbind();
        _telegraph.Reset();
        _nextBindAttempt = UnityEngine.Time.realtimeSinceStartup + BindRetrySeconds;
    }

    public override void OnUpdate()
    {
        MainThread.Pump();

        var now = UnityEngine.Time.realtimeSinceStartup;

        if (!_map.IsBound && now >= _nextBindAttempt)
        {
            _nextBindAttempt = now + BindRetrySeconds;
            if (_map.TryBind())
                MelonLogger.Msg("[AgentBridge] tactical map bound");
        }

        if (_map.IsBound && now >= _nextMapPoll)
        {
            _nextMapPoll = now + MapPollSeconds;
            try { _map.PollAndEmitEvents(); }
            catch (Exception ex) { MelonLogger.Warning($"[AgentBridge] map poll failed: {ex.Message}"); }
        }

        if (now >= _nextTelegraphPoll)
        {
            _nextTelegraphPoll = now + TelegraphPollSeconds;
            try { _telegraph.PollAndEmitEvents(); }
            catch (Exception ex) { MelonLogger.Warning($"[AgentBridge] telegraph poll failed: {ex.Message}"); }
        }
    }

    // ---- called from BridgeServer via MainThread.Run ----

    public StateSnapshotDto BuildSnapshot()
    {
        var snapshot = new StateSnapshotDto
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SceneBound = _map.IsBound,
            Teleprinters = _telegraph.ReadAll(),
            Guns = GunStateReader.ReadBoth(),
            Fcs = _fcs.ReadStatus(),
        };
        if (_map.IsBound)
        {
            var turretLocal = _map.TurretLocalOnMap();
            snapshot.TurretMapX = turretLocal.x;
            snapshot.TurretMapY = turretLocal.y;
            snapshot.Entities = _map.ReadEntities();
            snapshot.Markers = _map.ReadMarkers();
        }
        return snapshot;
    }

    public string QueueFireMission(FireMissionRequest req)
    {
        if (!string.IsNullOrEmpty(req.EntityId))
        {
            if (!_map.IsBound)
                return "tactical map not bound";
            var entity = _map.FindEntity(req.EntityId!);
            if (entity == null)
                return $"entity '{req.EntityId}' not visible on the command table (fog of war or bad id)";
            if (!_map.TryMoveMarker(req.MarkerId, entity.MapX, entity.MapY))
                return $"marker {req.MarkerId} not found on map";
            var result = _fcs.EnqueueFromMarker(req.MarkerId, req.Shell);
            if (result == "ok")
                EventLog.Append("fcs_task_update", "fcs",
                    $"fire mission queued on {req.EntityId} ({req.Shell}) via marker {req.MarkerId}");
            return result;
        }

        if (req.BearingDeg is float bearing && req.DistanceKm is float distance)
        {
            var result = _fcs.EnqueueByBearing(bearing, distance, req.Shell, req.MarkerId);
            if (result == "ok")
                EventLog.Append("fcs_task_update", "fcs",
                    $"fire mission queued at bearing {bearing:F1}°, {distance:F2} km ({req.Shell})");
            return result;
        }

        return "need either entityId or bearingDeg+distanceKm";
    }

    public bool PrintOnTeleprinter(string which, string[] lines)
    {
        var printer = which.Equals("primary", StringComparison.OrdinalIgnoreCase)
            ? Teleprinter.Teleprinters.Primary
            : Teleprinter.Teleprinters.Secondary;
        return _telegraph.Print(printer, lines);
    }
}
