using Il2Cpp;
using IronNestAgentBridge.Agent;
using IronNestAgentBridge.Fcs;
using IronNestAgentBridge.GameState;
using IronNestAgentBridge.Http;
using IronNestAgentBridge.Ui;
using MelonLoader;
using UnityEngine.InputSystem;

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
    private FdoAgent? _agent;
    private readonly AgentWindow _window = new();
    public MissionQueue MissionQueue { get; } = new();
    private float _nextDispatch;

    private float _nextBindAttempt;
    private float _nextMapPoll;
    private float _nextTelegraphPoll;
    private float _nextFcsSummary;
    private bool _autoStartDone;

    public string LastFcsSummary { get; private set; } = "";

    public override void OnInitializeMelon()
    {
        AgentConfig.Initialize();
        _agent = new FdoAgent(this);
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

    public override void OnDeinitializeMelon()
    {
        _agent?.Stop();
        _server?.Stop();
    }

    public override void OnGUI()
    {
        if (_agent != null)
            _window.Draw(_agent, this);
    }

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

        try
        {
            if (Keyboard.current != null && Keyboard.current.f10Key.wasPressedThisFrame)
                _window.Visible = !_window.Visible;
        }
        catch { }

        if (!_autoStartDone && _map.IsBound && AgentConfig.AutoStart && AgentConfig.LlmControl && _agent is { IsRunning: false })
        {
            _autoStartDone = true;
            _agent.Start();
        }

        if (now >= _nextDispatch)
        {
            _nextDispatch = now + 2f;
            try { DispatchFromQueue(); }
            catch (Exception ex) { MelonLogger.Warning($"[AgentBridge] dispatch failed: {ex.Message}"); }
        }

        if (now >= _nextFcsSummary)
        {
            _nextFcsSummary = now + 2f;
            try
            {
                var s = _fcs.ReadStatus();
                LastFcsSummary = $"FCS: pending={s.PendingCount} done={s.CompletedTaskCount} fail={s.FailedTaskCount}" +
                                 (s.LeftTask != null ? $"\nL: {s.LeftTask}" : "") +
                                 (s.RightTask != null ? $"\nR: {s.RightTask}" : "");
            }
            catch { }
        }
    }

    /// <summary>
    /// Drain the agent's priority queue into the FCS while its physical queue stays shallow.
    /// Runs on the main thread. Re-validates each target at dispatch time.
    /// </summary>
    private void DispatchFromQueue()
    {
        if (!_map.IsBound || MissionQueue.Count == 0)
            return;

        var status = _fcs.ReadStatus();
        if (!status.LogicLoaded || status.PendingCount >= AgentConfig.FcsQueueDepth)
            return;

        var slots = AgentConfig.FcsQueueDepth - status.PendingCount;
        for (var i = 0; i < slots; i++)
        {
            var mission = MissionQueue.PopBest();
            if (mission == null)
                return;

            // Dispatch-time revalidation: a staged entity strike is dropped if the target
            // died or slipped back into fog while waiting.
            if (!string.IsNullOrEmpty(mission.Request.EntityId) && _map.FindEntity(mission.Request.EntityId!) == null)
            {
                EventLog.Append("fcs_task_update", "fcs",
                    $"staged mission on {mission.Label} dropped: target destroyed or no longer visible");
                continue;
            }

            var result = QueueFireMission(mission.Request);
            EventLog.Append("fcs_task_update", "fcs",
                $"dispatched P{mission.Priority} {mission.Label} ({mission.Request.Shell}) -> {result}");
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

    private int _markerCursor;

    /// <summary>
    /// Round-robin over the map's marker tokens. Every queued mission moves a marker onto
    /// its aim point — cosmetic feedback so the player sees the agent's intent on the
    /// command table, exactly like a human dragging a red marker before pressing T.
    /// </summary>
    private int NextMarkerId()
    {
        var ids = _map.MarkerIds.OrderBy(i => i).ToList();
        if (ids.Count == 0) return -1;
        return ids[_markerCursor++ % ids.Count];
    }

    public string QueueFireMission(FireMissionRequest req)
    {
        if (!_map.IsBound)
            return "tactical map not bound";

        float mapX, mapY;
        string label;

        if (!string.IsNullOrEmpty(req.EntityId))
        {
            var entity = _map.FindEntity(req.EntityId!);
            if (entity == null)
                return $"entity '{req.EntityId}' not visible on the command table (fog of war or bad id)";
            mapX = entity.MapX;
            mapY = entity.MapY;
            label = req.EntityId!;
        }
        else if (req.BearingDeg is float bearing && req.DistanceKm is float distance)
        {
            var local = _map.SolutionToMapLocal(bearing, distance);
            mapX = local.x;
            mapY = local.y;
            label = $"bearing {bearing:F1}°, {distance:F2} km";
        }
        else
        {
            return "need either entityId or bearingDeg+distanceKm";
        }

        var markerId = NextMarkerId();
        if (markerId >= 0 && _map.TryMoveMarker(markerId, mapX, mapY))
        {
            var result = _fcs.EnqueueFromMarker(markerId, req.Shell, req.Priority);
            if (result == "ok")
                EventLog.Append("fcs_task_update", "fcs",
                    $"fire mission queued on {label} ({req.Shell}, P{req.Priority}) as marker {markerId}");
            return result;
        }

        // No marker available — fall back to direct injection, display-only loss.
        if (req.BearingDeg is float b2 && req.DistanceKm is float d2)
        {
            var result = _fcs.EnqueueByBearing(b2, d2, req.Shell, 0, req.Priority);
            if (result == "ok")
                EventLog.Append("fcs_task_update", "fcs",
                    $"fire mission queued at {label} ({req.Shell}), no marker available");
            return result;
        }
        return "no map marker available for entity targeting";
    }

    public bool PrintOnTeleprinter(string which, string[] lines)
    {
        var printer = which.Equals("primary", StringComparison.OrdinalIgnoreCase)
            ? Teleprinter.Teleprinters.Primary
            : Teleprinter.Teleprinters.Secondary;
        return _telegraph.Print(printer, lines);
    }
}
