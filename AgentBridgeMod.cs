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
    private readonly ImpactReader _impacts = new();
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

    public string LastFcsSummary { get; private set; } = "";

    /// <summary>
    /// Cutscene heuristic: the baseline gameplay camera (captured at scene bind) has been
    /// swapped out or disabled — cinematics always cut cameras. Panel hides and the agent
    /// pauses while true.
    /// </summary>
    public static volatile bool CinematicActive;
    private UnityEngine.Camera? _baselineCamera;
    private float _nextCinematicCheck;

    private void UpdateCinematicState()
    {
        var cam = UnityEngine.Camera.main;
        if (_baselineCamera == null)
        {
            if (_map.IsBound && cam != null)
                _baselineCamera = cam;
            CinematicActive = false;
            return;
        }

        var active = cam == null || !ReferenceEquals(cam, _baselineCamera);
        if (active != CinematicActive)
        {
            CinematicActive = active;
            MelonLogger.Msg($"[AgentBridge] cinematic {(active ? "started" : "ended")} (main camera: {(cam == null ? "none" : cam.name)})");
            EventLog.Append("cinematic", "game", active ? "cinematic started" : "cinematic ended");
        }
    }

    /// <summary>Mirrors Application.isFocused for background threads; agent pauses while false.</summary>
    public static volatile bool GameFocused = true;

    public override void OnInitializeMelon()
    {
        AgentConfig.Initialize();
        RequisitionOperator.RequisitionLockProvider = () => _fcs.GetRequisitionLock();
        _agent = new FdoAgent(this);
        if (AgentConfig.EnableHttpApi)
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
        else
        {
            MelonLogger.Msg("[AgentBridge] HTTP API disabled (EnableHttpApi=false)");
        }
    }

    public override void OnDeinitializeMelon()
    {
        _agent?.Stop();
        _server?.Stop();
    }

    public override void OnGUI()
    {
        // Mirror FCS's implicit behavior (no HUD until the scene binds) plus an explicit
        // camera-swap cinematic gate that also covers mid-mission cutscenes.
        if (_agent != null && _map.IsBound && !CinematicActive)
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
        GameFocused = UnityEngine.Application.isFocused;
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
            try { _impacts.PollAndEmitEvents(_map.MapSurface); }
            catch { }
        }

        if (now >= _nextTelegraphPoll)
        {
            _nextTelegraphPoll = now + TelegraphPollSeconds;
            try { _telegraph.PollAndEmitEvents(); }
            catch (Exception ex) { MelonLogger.Warning($"[AgentBridge] telegraph poll failed: {ex.Message}"); }
        }

        try
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.f10Key.wasPressedThisFrame)
                    _window.Visible = !_window.Visible;
                if (kb.f11Key.wasPressedThisFrame)
                    ToggleLlmControl();
                // F7, not F12 — F12 is Steam's screenshot key and kept silently toggling this.
                if (kb.f7Key.wasPressedThisFrame)
                    AgentConfig.PriorityQueue = !AgentConfig.PriorityQueue;
                // F9 is FCS's plan reset; ride the same semantic — full agent reset.
                if (kb.f9Key.wasPressedThisFrame)
                    FullReset("F9");
            }
        }
        catch { }

        if (now >= _nextDispatch)
        {
            _nextDispatch = now + 2f;
            try { DispatchFromQueue(); }
            catch (Exception ex) { MelonLogger.Warning($"[AgentBridge] dispatch failed: {ex.Message}"); }
        }

        if (now >= _nextCinematicCheck)
        {
            _nextCinematicCheck = now + 0.5f;
            try { UpdateCinematicState(); }
            catch { }
            try { DetectManualCalibration(); }
            catch { }
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
                ReturnFinishedMarkers(s);

                var cardResult = _fcs.ReadConsoleCardResult();
                if (!string.IsNullOrEmpty(cardResult) && cardResult != _lastCardResult)
                {
                    _lastCardResult = cardResult!;
                    EventLog.Append("requisition", "fcs", $"card request completed: {cardResult}");
                    Agent.TransactionLog.Write("requisition", cardResult!);
                }
            }
            catch { }
        }
    }

    // marker id -> the target label its current mission covers (entityId or bearing/distance)
    private readonly Dictionary<int, string> _deployedMarkers = new();
    private static readonly System.Text.RegularExpressions.Regex TaskIdRe =
        new(@"^T(\d+)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Park markers back at their home spot once no FCS task references them anymore.</summary>
    private void ReturnFinishedMarkers(FcsStatusDto status)
    {
        if (_deployedMarkers.Count == 0 || !_map.IsBound)
            return;

        var inUse = new HashSet<int>();
        void Scan(string? desc)
        {
            if (desc == null) return;
            var m = TaskIdRe.Match(desc);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var id))
                inUse.Add(id);
        }
        Scan(status.LeftTask);
        Scan(status.RightTask);
        foreach (var t in status.PendingTasks)
            Scan(t);

        foreach (var id in _deployedMarkers.Keys.Where(id => !inUse.Contains(id)).ToList())
        {
            if (_map.ReturnMarkerHome(id))
                _deployedMarkers.Remove(id);
        }
    }

    /// <summary>Append the covered target's label to FCS task strings so the agent can correlate.</summary>
    private string? AnnotateTask(string? desc)
    {
        if (desc == null) return null;
        var m = TaskIdRe.Match(desc);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var id) && _deployedMarkers.TryGetValue(id, out var label))
            return $"{desc} → {label}";
        return desc;
    }

    public void ToggleLlmControl()
    {
        AgentConfig.LlmControl = !AgentConfig.LlmControl;
        if (_agent == null) return;
        if (AgentConfig.LlmControl && !_agent.IsRunning) _agent.Start();
        else if (!AgentConfig.LlmControl && _agent.IsRunning) _agent.Stop();
        MelonLogger.Msg($"[AgentBridge] LLM control {(AgentConfig.LlmControl ? "ON" : "OFF")}");
    }

    /// <summary>
    /// F9-style full reset: stop the agent, drop staged missions and conversation state,
    /// rebind the scene. The agent restarts only if LLM control is enabled.
    /// </summary>
    public void FullReset(string reason)
    {
        MelonLogger.Msg($"[AgentBridge] full reset ({reason})");
        Agent.TransactionLog.Write("reset", $"full reset: {reason}");
        _agent?.Stop();
        _agent?.ClearLog();
        EventLog.Clear(); // stale events must not replay into the restarted agent's fresh context
        _lastCardResult = "";
        MissionQueue.Clear();
        _deployedMarkers.Clear();
        _map.Unbind();
        _impacts.Reset();
        _telegraph.Reset();
        _baselineCamera = null;
        TurretCalibrated = false;
        _lastPieceLocal = null;
        _nextBindAttempt = UnityEngine.Time.realtimeSinceStartup + 1f;
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
            Cards = AmmoReader.ReadCards(),
        };
        snapshot.AvailableShells = snapshot.Cards.Select(c => c.Id).ToList();
        var cardIds = new HashSet<string>(snapshot.AvailableShells, StringComparer.OrdinalIgnoreCase);
        snapshot.ShellSpecs = AmmoReader.ReadShellSpecs().Where(s => cardIds.Contains(s.Id)).ToList();
        snapshot.Fcs.LeftTask = AnnotateTask(snapshot.Fcs.LeftTask);
        snapshot.Fcs.RightTask = AnnotateTask(snapshot.Fcs.RightTask);
        snapshot.Fcs.PendingTasks = snapshot.Fcs.PendingTasks.Select(t => AnnotateTask(t)!).ToList();
        if (_map.IsBound)
        {
            var turretLocal = _map.TurretLocalOnMap();
            snapshot.TurretMapX = turretLocal.x;
            snapshot.TurretMapY = turretLocal.y;
            snapshot.TurretCalibrated = TurretCalibrated;
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

    /// <summary>Card purchase: DTO into FCS's coordinator when available, legacy physical path otherwise.</summary>
    public string RequestCard(string cardId, float? bearingDeg, int priority = 50, string? startGrid = null)
    {
        var viaFcs = _fcs.RequestCardPurchase(cardId, bearingDeg, priority, startGrid);
        if (viaFcs != null)
        {
            EventLog.Append("requisition", "fcs", $"card '{cardId}' {viaFcs}");
            return viaFcs + " (result arrives via events)";
        }
        return GameState.RequisitionOperator.StartPurchase(cardId, bearingDeg, null);
    }

    private string _lastCardResult = "";

    public string CancelPendingFcsTask(int targetId)
    {
        var result = _fcs.CancelPending(targetId);
        EventLog.Append("fcs_task_update", "fcs", $"cancel T{targetId}: {result}");
        return result;
    }

    // Calibration is an act, not a position property: true once someone (agent tool or a
    // manual drag we detect) has deliberately placed the piece this mission.
    public bool TurretCalibrated { get; private set; }
    private UnityEngine.Vector3? _lastPieceLocal;

    private void DetectManualCalibration()
    {
        if (!_map.IsBound) return;
        var local = _map.TurretLocalOnMap();
        if (_lastPieceLocal is { } prev
            && (Math.Abs(local.x - prev.x) > 0.02f || Math.Abs(local.y - prev.y) > 0.02f)
            && !TurretCalibrated)
        {
            TurretCalibrated = true; // player dragged the piece — counts as calibration
            EventLog.Append("turret_position", "map", "turret piece was moved manually — treated as calibrated");
        }
        _lastPieceLocal = local;
    }

    public UnityEngine.Vector3 ReadTurretLocal() => _map.TurretLocalOnMap();

    public MapEntityDto? FindVisibleEntity(string entityId) => _map.FindEntity(entityId);

    public string SetDeclaredTurret(float kmX, float kmY)
    {
        if (!Agent.GridMath.InMapBounds((kmX, kmY)))
            return $"km({kmX:F1},{kmY:F1}) is outside the map — rejected (check the grid conversion)";
        // The map origin is the unplaced-piece sentinel; "calibrating" to it is always the
        // model echoing the snapshot's placeholder value back, never a real position.
        if (Math.Abs(kmX - 10.016f) < 0.15f && Math.Abs(kmY - 5.235f) < 0.15f)
            return "km(10.02,5.24) 是地图原点(未校准哨兵值), 不是真实炮位 — rejected。校准依据只能是统帅部电文里的铁巢网格";
        var result = _map.SetDeclaredTurret(kmX, kmY);
        if (!result.Contains("not") && !result.Contains("rejected"))
        {
            TurretCalibrated = true;
            _lastPieceLocal = _map.TurretLocalOnMap();
        }
        EventLog.Append("turret_position", "map", result);
        return result;
    }

    public string QueueFireMission(FireMissionRequest req)
    {
        if (!_map.IsBound)
            return "tactical map not bound";

        float mapX, mapY;
        string label;
        var aimDerivedFromTurret = false;

        if (!string.IsNullOrEmpty(req.EntityId))
        {
            var entity = _map.FindEntity(req.EntityId!);
            if (entity == null)
                return $"entity '{req.EntityId}' not visible on the command table (fog of war or bad id)";
            mapX = entity.MapX;
            mapY = entity.MapY;
            label = req.EntityId!;
        }
        else if (!string.IsNullOrEmpty(req.TargetPoint))
        {
            var turretLocal = _map.TurretLocalOnMap();
            var turretKm = ((double)(10.016f + turretLocal.x * 3.8164f), (double)(5.235f + turretLocal.y * 3.8164f));
            if (Agent.GridMath.ParsePoint(req.TargetPoint!, turretKm) is not { } km)
                return $"cannot parse target '{req.TargetPoint}' (grid like 'K4 5:0' or 'kmX,kmY')";
            mapX = (float)((km.x - 10.016) / 3.8164);
            mapY = (float)((km.y - 5.235) / 3.8164);
            label = req.TargetPoint!;
        }
        else if (req.BearingDeg is float bearing && req.DistanceKm is float distance)
        {
            var local = _map.SolutionToMapLocal(bearing, distance);
            mapX = local.x;
            mapY = local.y;
            label = $"bearing {bearing:F1}°, {distance:F2} km";
            aimDerivedFromTurret = true;
        }
        else
        {
            return "need entityId, target, or bearingDeg+distanceKm";
        }

        var offX = req.OffsetKmX ?? 0f;
        var offY = req.OffsetKmY ?? 0f;
        if (Math.Abs(offX) > 0.5f || Math.Abs(offY) > 0.5f)
            return "offset exceeds ±0.5km — offsets are for nudging the burst clear of friendlies; aim at different coordinates instead";
        if (offX != 0f || offY != 0f)
        {
            mapX += offX / 3.8164f;
            mapY += offY / 3.8164f;
            label += $" 偏移({offX:+0.00;-0.00},{offY:+0.00;-0.00})km";
        }

        // Defense in depth: never fling a marker off the table on an out-of-bounds solution.
        var kmXCheck = 10.016f + mapX * 3.8164f;
        var kmYCheck = 5.235f + mapY * 3.8164f;
        if (!Agent.GridMath.InMapBounds((kmXCheck, kmYCheck)))
        {
            // target/entityId aims are absolute coordinates — the turret never enters the
            // math, so OOB means bad params. Only bearing/distance aims derive from the
            // assumed turret origin, where an off/OOB origin can also be the cause.
            return aimDerivedFromTurret
                ? $"aim point km({kmXCheck:F1},{kmYCheck:F1}) is outside the map — rejected. " +
                  "This aim derives from the ASSUMED turret position + bearing/distance: either the params are wrong, " +
                  "or the assumed turret position is off/OOB — check get_assumed_turret_position and recalibrate if unreliable"
                : $"target coordinates km({kmXCheck:F1},{kmYCheck:F1}) are outside the map — rejected. " +
                  "Bad fire params (grid/km parse or triangulation error); the turret position is irrelevant to this path";
        }
        var spec = AmmoReader.ReadShellSpecs().FirstOrDefault(x => string.Equals(x.Id, req.Shell, StringComparison.OrdinalIgnoreCase));
        var maxRange = spec?.ChargeRanges.Count > 0 ? spec.ChargeRanges.Max(c => c.MaxKm) : 40f;
        if (req.DistanceKm is { } dist && dist > maxRange)
            return $"distance {dist:F1}km exceeds {req.Shell} max range {maxRange:F1}km — rejected";

        // Blast-radius survey around the final aim point: friendlies inside the radius block
        // the mission (soft — confirmFriendlyFire overrides); visible hostiles inside it are
        // reported back so the LLM can verify a merged strike actually covers its cluster.
        var suffix = "";
        var blastKm = (spec?.ImpactRadius ?? 0f) / 1000f;
        if (blastKm > 0.001f)
        {
            var friendliesInside = new List<string>();
            var friendliesNear = new List<string>();
            var hostilesCovered = new List<string>();
            foreach (var e in _map.ReadEntities())
            {
                if (!e.IsAlive) continue;
                var dx = 10.016f + e.MapX * 3.8164f - kmXCheck;
                var dy = 5.235f + e.MapY * 3.8164f - kmYCheck;
                var dKm = MathF.Sqrt(dx * dx + dy * dy);
                var friendly = e.Role.Contains("Ally") || e.Role == "Spotter"
                               || e.Id.Contains("civil", StringComparison.OrdinalIgnoreCase)
                               || e.RawId.Contains("civil", StringComparison.OrdinalIgnoreCase);
                if (friendly && dKm <= blastKm)
                    friendliesInside.Add($"{e.Id}({e.Role},距弹着{dKm:F2}km)");
                else if (friendly && dKm <= blastKm * 1.5f)
                    friendliesNear.Add($"{e.Id}({dKm:F2}km)");
                else if (!friendly && dKm <= blastKm)
                    hostilesCovered.Add($"{e.Id}({dKm:F2}km)");
            }

            if (friendliesInside.Count > 0 && !req.ConfirmFriendlyFire)
                return $"友军误伤警告 — 已拒绝: {string.Join(", ", friendliesInside)} 在弹着点km({kmXCheck:F2},{kmYCheck:F2})" +
                       $"的{req.Shell}爆炸半径{blastKm * 1000f:F0}m内。用offsetKmX/offsetKmY把弹着点向远离友军一侧移出半径" +
                       "(会牺牲部分毁伤), 或换更小爆炸半径的弹种; 确认接受误伤才用confirmFriendlyFire=true重试";
            if (friendliesInside.Count > 0)
                suffix += $"; 警告: 已确认误伤风险, 友军在爆炸半径内: {string.Join(", ", friendliesInside)}";
            else if (friendliesNear.Count > 0)
                suffix += $"; 注意: 友军贴近弹着点(≤1.5×爆炸半径): {string.Join(", ", friendliesNear)}";
            if (hostilesCovered.Count > 0)
                suffix += $"; 爆炸半径({blastKm * 1000f:F0}m)可同时覆盖: {string.Join(", ", hostilesCovered)}";
        }

        var markerId = NextMarkerId();
        if (markerId >= 0 && _map.TryMoveMarker(markerId, mapX, mapY))
        {
            var result = _fcs.EnqueueFromMarker(markerId, req.Shell, req.Priority);
            if (result == "ok")
            {
                _deployedMarkers[markerId] = label;
                EventLog.Append("fcs_task_update", "fcs",
                    $"fire mission queued on {label} ({req.Shell}, P{req.Priority}) as marker {markerId}");
                return "ok" + suffix;
            }
            return result;
        }

        // No marker available — fall back to direct injection, display-only loss.
        // Solution re-derived from the FINAL aim point so an offset survives this path too.
        if (req.BearingDeg is float && req.DistanceKm is float)
        {
            var turretLocal = _map.TurretLocalOnMap();
            var ddx = mapX - turretLocal.x;
            var ddy = mapY - turretLocal.y;
            var b2 = (MathF.Atan2(ddx, ddy) * 57.29578f % 360f + 360f) % 360f;
            var d2 = MathF.Sqrt(ddx * ddx + ddy * ddy) * 3.8164f;
            var result = _fcs.EnqueueByBearing(b2, d2, req.Shell, 0, req.Priority);
            if (result == "ok")
            {
                EventLog.Append("fcs_task_update", "fcs",
                    $"fire mission queued at {label} ({req.Shell}), no marker available");
                return "ok" + suffix;
            }
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
