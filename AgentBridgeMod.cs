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

    private MissionManager.GamePhase? _lastPhase;

    private bool _cbWasRunning;
    private float _nextCbTick;

    /// <summary>Game clock in seconds-of-day, mirrored for motion-model timestamps.</summary>
    public static volatile float MissionClockSeconds;

    private GenericTimerSceneSync? _worldClock;

    /// <summary>
    /// Mirror the in-game 24h world clock (GenericTimerSceneSync, the bunker wall clock —
    /// the same clock telegraph messages reference) into EventLog.GameClock as "HH:mm".
    /// Falls back to the mission stopwatch when no world clock exists in the scene.
    /// </summary>
    private void UpdateGameClock()
    {
        try
        {
            if (_worldClock == null)
            {
                foreach (var sync in UnityEngine.Object.FindObjectsOfType<GenericTimerSceneSync>())
                {
                    if (_worldClock == null || sync.CurrentTime > _worldClock.CurrentTime)
                        _worldClock = sync;
                    MelonLogger.Msg($"[AgentBridge] world clock candidate '{sync.TimerID}' t={sync.CurrentTime:F0}s");
                }
            }
            if (_worldClock != null)
            {
                var t = _worldClock.CurrentTime;
                if (t > 0f)
                {
                    MissionClockSeconds = t;
                    EventLog.GameClock = $"{(int)(t / 3600) % 24:00}:{(int)(t / 60) % 60:00}";
                    return;
                }
            }
        }
        catch { _worldClock = null; }

        try
        {
            var tracker = MissionStatsTracker.Instance;
            if (tracker == null || !tracker.timerRunning)
                return;
            var t = tracker.timerValue;
            MissionClockSeconds = t;
            EventLog.GameClock = $"{(int)(t / 60):00}:{(int)(t % 60):00}";
        }
        catch { }
    }

    private sealed record InFlightShell(string Label, string Shell, float KmX, float KmY, float FiredAt,
        string FiredAtGame = "", int Serial = 0, float FlightEtaSeconds = 60f);
    private readonly List<InFlightShell> _inFlight = new();
    private const float InFlightTimeoutSeconds = 150f;
    private const float ImpactMatchKm = 3f;

    /// <summary>
    /// An actual impact landed: resolve the nearest in-flight shell within range and hand its
    /// identity back so the shell_impact event names the task (#N) that just landed.
    /// </summary>
    private string? OnShellImpact(float kmX, float kmY)
    {
        InFlightShell? best = null;
        var bestDist = ImpactMatchKm;
        foreach (var s in _inFlight)
        {
            var d = MathF.Sqrt((s.KmX - kmX) * (s.KmX - kmX) + (s.KmY - kmY) * (s.KmY - kmY));
            if (d < bestDist) { bestDist = d; best = s; }
        }
        if (best == null)
            return null;
        _inFlight.Remove(best);
        return $"#{best.Serial} {best.Label} ({best.Shell})";
    }

    /// <summary>
    /// The per-gun impact marker cannot signal a repeat impact on the SAME spot (it simply
    /// does not move), so an in-flight shell that outlives its expected flight time is
    /// presumed landed and settled WITH an event — never a silent expiry the agent
    /// misreads as "still flying".
    /// </summary>
    private void ResolveOverdueShells()
    {
        if (_inFlight.Count == 0)
            return;
        var now = UnityEngine.Time.realtimeSinceStartup;
        foreach (var s in _inFlight.Where(s => now - s.FiredAt > Math.Min(s.FlightEtaSeconds, InFlightTimeoutSeconds)).ToList())
        {
            _inFlight.Remove(s);
            EventLog.Append("shell_impact", "map",
                $"弹着推定: #{s.Serial} {s.Label} ({s.Shell}) 已超预计飞行时间, 判定已落地并销账 — 弹着标记未移动通常=与前一发落点几乎重合; 可重新评估该目标");
        }
    }

    public List<string> DescribeInFlight()
    {
        var now = UnityEngine.Time.realtimeSinceStartup;
        return _inFlight.Select(s =>
            $"#{s.Serial} {s.Label} ({s.Shell}, 出膛@{(s.FiredAtGame.Length > 0 ? s.FiredAtGame : "?")}, 已飞{now - s.FiredAt:F0}s/预计{s.FlightEtaSeconds:F0}s)").ToList();
    }

    /// <summary>
    /// Counter-battery countdown relay: the bunker timer the player can see/hear, pushed to
    /// the agent as counter_battery events — on start, every 20 s while running, on expiry
    /// and on permanent stop. Zero means enemy fire lands on this position.
    /// </summary>
    private void PollCounterBattery(float now)
    {
        CounterBatteryTimer? timer = null;
        try { timer = CounterBatteryTimer.Instance; } catch { }
        if (timer == null)
        {
            _cbWasRunning = false;
            return;
        }

        bool running, expired, stopped;
        float remaining;
        try
        {
            running = timer.IsRunning;
            expired = timer.IsExpired;
            stopped = timer.IsPermanentlyStopped;
            remaining = timer.TimeRemaining;
        }
        catch { return; }

        static string Fmt(float s) => $"{(int)(s / 60):00}:{(int)(s % 60):00}";

        if (stopped)
        {
            if (_cbWasRunning)
                EventLog.Append("counter_battery", "game", "反炮击倒计时已永久解除 — 威胁排除");
            _cbWasRunning = false;
            return;
        }
        if (expired)
        {
            if (_cbWasRunning)
                EventLog.Append("counter_battery", "game", "反炮击倒计时归零 — 敌炮火正在覆盖本阵地");
            _cbWasRunning = false;
            return;
        }
        if (!running)
        {
            _cbWasRunning = false;
            return;
        }

        if (!_cbWasRunning)
        {
            _cbWasRunning = true;
            _nextCbTick = now + 20f;
            EventLog.Append("counter_battery", "game",
                $"反炮击倒计时启动: 剩余 {Fmt(remaining)} — 归零时敌炮火覆盖本阵地");
        }
        else if (now >= _nextCbTick)
        {
            _nextCbTick = now + 20f;
            EventLog.Append("counter_battery", "game", $"反炮击倒计时: 剩余 {Fmt(remaining)}");
        }
    }

    /// <summary>
    /// Mission lifecycle automation off MissionManager.CurrentPhase:
    /// leaving MissionActive (summary screen / back to map / menu) auto-stops the agent so it
    /// doesn't burn tokens against a dead battlefield; entering MissionActive wipes the previous
    /// mission's conversation and event log — stale intel from the last map is worse than none.
    /// The agent never auto-starts: F11 remains the per-session opt-in.
    /// </summary>
    private void UpdateMissionPhase()
    {
        MissionManager.GamePhase phase;
        try
        {
            var mm = MissionManager.Instance;
            if (mm == null) return;
            phase = mm.CurrentPhase;
        }
        catch { return; }

        if (_lastPhase == phase)
            return;
        var prev = _lastPhase;
        _lastPhase = phase;
        if (prev == null)
            return; // first sample after boot — no transition to act on

        if (prev == MissionManager.GamePhase.MissionActive)
        {
            MelonLogger.Msg($"[AgentBridge] mission ended ({prev}->{phase}) — agent auto-stop");
            Agent.TransactionLog.Write("mission", $"mission ended ({prev}->{phase}); agent auto-stopped");
            if (AgentConfig.LlmControl)
                AgentConfig.LlmControl = false;
            if (_agent?.IsRunning == true)
                _agent.Stop();
        }

        if (phase == MissionManager.GamePhase.MissionActive)
            FullReset("new mission — clearing previous conversation");
    }

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
        Agent.GridMath.ResetMapBounds();
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
            {
                // Per-mission firing envelope: the real map sheet size, not the A..Z guess.
                if (_map.KmBounds is { } kb)
                {
                    Agent.GridMath.SetMapBoundsKm(kb.MinX, kb.MinY, kb.MaxX, kb.MaxY);
                    MelonLogger.Msg($"[AgentBridge] tactical map bound; sheet extent km({kb.MinX:F1},{kb.MinY:F1})-({kb.MaxX:F1},{kb.MaxY:F1})");
                }
                else
                {
                    Agent.GridMath.ResetMapBounds();
                    MelonLogger.Msg("[AgentBridge] tactical map bound; sheet unmeasured — generous bounds fallback");
                }
            }
        }

        if (_map.IsBound && now >= _nextMapPoll)
        {
            _nextMapPoll = now + MapPollSeconds;
            try { _map.PollAndEmitEvents(); }
            catch (Exception ex) { MelonLogger.Warning($"[AgentBridge] map poll failed: {ex.Message}"); }
            try { _impacts.PollAndEmitEvents(_map.MapSurface, OnShellImpact); }
            catch { }
            ResolveOverdueShells();
            PollFriendlyIntrusions(now);
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
                // F9 is FCS's plan reset; ride the same semantic — full agent reset.
                if (kb.f9Key.wasPressedThisFrame)
                    FullReset("F9");
            }
        }
        catch { }

        if (now >= _nextCinematicCheck)
        {
            _nextCinematicCheck = now + 0.5f;
            try { UpdateCinematicState(); }
            catch { }
            try { DetectManualCalibration(); }
            catch { }
            try { UpdateMissionPhase(); }
            catch { }
            try { PollCounterBattery(now); }
            catch { }
            try { UpdateGameClock(); }
            catch { }
        }

        if (now >= _nextFcsSummary)
        {
            _nextFcsSummary = now + 2f;
            try
            {
                var s = _fcs.ReadStatus();
                LastFcsSummary = $"FCS: pending={s.PendingCount} done={s.CompletedTaskCount} fail={s.FailedTaskCount}" +
                                 (s.LeftTask != null ? $"\nT1(左): {s.LeftTask}" : "") +
                                 (s.RightTask != null ? $"\nT2(右): {s.RightTask}" : "");
                TrackFiredShells(s);

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

    // task serial (#N) -> the mission it covers (label + shell + aim point). No physical
    // marker involved: map tokens belong to the player (T3+) and FCS's gun indicators (T1/T2).
    private readonly Dictionary<int, InFlightShell> _deployedTasks = new();
    private static readonly System.Text.RegularExpressions.Regex TaskSerialRe =
        new(@"^#(\d+)\b", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A bookkept task that vanished from the live FCS set has fired (or was cleared) — move it to in-flight.</summary>
    private void TrackFiredShells(FcsStatusDto status)
    {
        if (_deployedTasks.Count == 0)
            return;

        // Live serial set comes structured from the gateway; display strings are never parsed.
        var live = status.SerialToMarker;

        foreach (var serial in _deployedTasks.Keys.Where(s => !live.ContainsKey(s)).ToList())
        {
            // The task left pending and both gun slots: the shell is in the air (or the task
            // was cancelled — rare, agent-initiated, self-corrects on timeout). Track it so
            // the agent doesn't re-queue a target whose shell is still flying.
            var dep = _deployedTasks[serial];
            _deployedTasks.Remove(serial);
            _inFlight.Add(dep with { FiredAt = UnityEngine.Time.realtimeSinceStartup, FiredAtGame = EventLog.GameClock });
            EventLog.Append("shell_fired", "fcs",
                $"炮弹出膛: #{dep.Serial} {dep.Label} ({dep.Shell}) 已在飞行途中, 等待弹着 — 勿重复排队该目标");
        }
    }

    // The queue-time friendly-fire intercept only sees the situation AT queue time; the
    // front line moves. Watch every pending task's blast zone for friendlies that arrived
    // afterwards and warn the agent once per intrusion (cleared when the zone is clean or
    // the task leaves), so it can adjust_fire away or cancel before the shot.
    private readonly HashSet<int> _ffWarned = new();
    private float _nextFfSweep;

    private void PollFriendlyIntrusions(float now)
    {
        if (now < _nextFfSweep || _deployedTasks.Count == 0 || !_map.IsBound)
            return;
        _nextFfSweep = now + 5f;

        List<MapEntityDto> entities;
        List<ShellSpecDto> specs;
        try
        {
            entities = _map.ReadEntities();
            specs = AmmoReader.ReadShellSpecs();
        }
        catch { return; }

        foreach (var (serial, task) in _deployedTasks.ToList())
        {
            var blastKm = specs.FirstOrDefault(x => string.Equals(x.Id, task.Shell, StringComparison.OrdinalIgnoreCase))?.ImpactRadius ?? 0f;
            if (blastKm <= 0.001f)
                continue;

            var inside = new List<string>();
            foreach (var e in entities)
            {
                if (!e.IsAlive) continue;
                var friendly = e.Role.Contains("Ally") || e.Role == "Spotter"
                               || e.Id.Contains("civil", StringComparison.OrdinalIgnoreCase)
                               || e.RawId.Contains("civil", StringComparison.OrdinalIgnoreCase);
                if (!friendly) continue;
                var dx = 10.016f + e.MapX * 3.8164f - task.KmX;
                var dy = 5.235f + e.MapY * 3.8164f - task.KmY;
                if (MathF.Sqrt(dx * dx + dy * dy) <= blastKm)
                    inside.Add(e.Id);
            }

            if (inside.Count > 0)
            {
                if (_ffWarned.Add(serial))
                    EventLog.Append("friendly_warning", "map",
                        $"⚠误伤预警: 已排任务 #{serial} {task.Label} 的弹着区({task.Shell}半径{blastKm * 1000f:F0}m)内" +
                        $"现有友军 {string.Join(", ", inside)} — 立即adjust_fire挪开弹着点或cancel_pending_task");
            }
            else
            {
                _ffWarned.Remove(serial);
            }
        }
        _ffWarned.RemoveWhere(s => !_deployedTasks.ContainsKey(s));
    }

    /// <summary>Append the covered target's label to FCS task strings so the agent can correlate.</summary>
    private string? AnnotateTask(string? desc)
    {
        if (desc == null) return null;
        var m = TaskSerialRe.Match(desc);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var serial)
            && _deployedTasks.TryGetValue(serial, out var dep))
            return $"{desc} → {dep.Label}";
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
        _deployedTasks.Clear();
        _inFlight.Clear();
        _map.Unbind();
        Agent.GridMath.ResetMapBounds();
        _impacts.Reset();
        _telegraph.Reset();
        _baselineCamera = null;
        _worldClock = null;
        _cbWasRunning = false;
        TurretCalibrated = false;
        _lastPieceLocal = null;
        _nextBindAttempt = UnityEngine.Time.realtimeSinceStartup + 1f;
    }

    // ---- called from BridgeServer via MainThread.Run ----

    public StateSnapshotDto BuildSnapshot()
    {
        var snapshot = new StateSnapshotDto
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            GameTime = EventLog.GameClock,
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
        snapshot.InFlightShells = DescribeInFlight();
        if (_map.IsBound)
        {
            snapshot.MapExtentKm = Agent.GridMath.MapBoundsText;
            var turretLocal = _map.TurretLocalOnMap();
            snapshot.TurretMapX = turretLocal.x;
            snapshot.TurretMapY = turretLocal.y;
            snapshot.TurretCalibrated = TurretCalibrated;
            snapshot.Entities = _map.ReadEntities();
            snapshot.Markers = _map.ReadMarkers();
        }
        return snapshot;
    }

    /// <summary>Card purchase: DTO into FCS's coordinator when available, legacy physical path otherwise.</summary>
    public string RequestCard(string cardId, float? bearingDeg, int priority = 50, string? startGrid = null,
        float? distanceKm = null)
    {
        var viaFcs = _fcs.RequestCardPurchase(cardId, bearingDeg, priority, startGrid, distanceKm);
        if (viaFcs != null)
        {
            EventLog.Append("requisition", "fcs", $"card '{cardId}' {viaFcs}");
            return viaFcs + " (result arrives via events)";
        }
        return GameState.RequisitionOperator.StartPurchase(cardId, bearingDeg, null);
    }

    private string _lastCardResult = "";

    public string CancelPendingFcsTask(int serial)
    {
        var result = _fcs.CancelPending(serial);
        EventLog.Append("fcs_task_update", "fcs", $"cancel #{serial}: {result}");
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

        var suffix = SurveyBlast(req.Shell, kmXCheck, kmYCheck, req.AllowDangerouslyFriendlyFire, out var ffRejection);
        if (ffRejection != null)
            return ffRejection;

        // Moving-target motion model (telegraph intel): transcribe into the map-local
        // linear function the patched FCS extrapolates each planning round.
        Fcs.FcsGateway.MotionSpec? motion = null;
        if (!string.IsNullOrEmpty(req.MotionFrom))
        {
            if (Agent.GridMath.ParsePoint(req.MotionFrom!, (10.016, 5.235)) is not { } m0)
                return $"cannot parse motionFrom '{req.MotionFrom}'";
            if (req.MotionBearingDeg is not { } mb || req.MotionSpeedKmh is not { } mv)
                return "motionFrom requires motionBearingDeg and motionSpeedKmh";
            var t0 = MissionClockSeconds;
            if (!string.IsNullOrWhiteSpace(req.MotionAtTime))
            {
                var parts = req.MotionAtTime!.Split(':');
                if (parts.Length is < 2 or > 3
                    || !int.TryParse(parts[0], out var hh) || !int.TryParse(parts[1], out var mm)
                    || (parts.Length == 3 && !int.TryParse(parts[2], out _)))
                    return $"cannot parse motionAtTime '{req.MotionAtTime}' (expect 24h \"HH:mm\", same clock as event stamps)";
                var ss = parts.Length == 3 && int.TryParse(parts[2], out var s3) ? s3 : 0;
                t0 = hh * 3600 + mm * 60 + ss;
            }
            var rad = mb * MathF.PI / 180f;
            var speedLocalPerSec = mv / 3600f / 3.8164f;
            motion = new Fcs.FcsGateway.MotionSpec(
                (float)((m0.x - 10.016) / 3.8164), (float)((m0.y - 5.235) / 3.8164),
                MathF.Sin(rad) * speedLocalPerSec, MathF.Cos(rad) * speedLocalPerSec, t0);
        }

        // Pure aim-point enqueue: no physical marker is touched — the map tokens belong to
        // the player (T3+) and to FCS's own gun indicators (T1/T2). The task is late-bound
        // via aimLocal; initial bearing/distance derive from the final aim vs the piece.
        {
            var turretLocal = _map.TurretLocalOnMap();
            var ddx = mapX - turretLocal.x;
            var ddy = mapY - turretLocal.y;
            var brg = (MathF.Atan2(ddx, ddy) * 57.29578f % 360f + 360f) % 360f;
            var distKm = MathF.Sqrt(ddx * ddx + ddy * ddy) * 3.8164f;
            var result = _fcs.EnqueueAimPoint(mapX, mapY, brg, distKm, req.Shell, req.Priority,
                out var serial, req.EntityId, motion);
            if (result == "ok")
            {
                if (serial > 0)
                    _deployedTasks[serial] = new InFlightShell(label, req.Shell, kmXCheck, kmYCheck, 0f,
                        Serial: serial, FlightEtaSeconds: distKm / 0.4f + 25f);
                EventLog.Append("fcs_task_update", "fcs",
                    $"fire mission queued on {label} ({req.Shell}, P{req.Priority}) as #{serial}");
                return $"ok (#{serial}){suffix}";
            }
            return result;
        }
    }

    /// <summary>
    /// Blast-radius survey around an aim point: friendlies inside the radius set `rejection`
    /// (soft block — allowDanger overrides); visible hostiles inside it are reported in the
    /// returned suffix so the LLM can verify a merged strike actually covers its cluster.
    /// Unknown shell (null/unmatched) surveys nothing — empty suffix, no rejection.
    /// </summary>
    private string SurveyBlast(string? shell, float kmX, float kmY, bool allowDanger, out string? rejection)
    {
        rejection = null;
        var suffix = "";
        var spec = AmmoReader.ReadShellSpecs().FirstOrDefault(x => string.Equals(x.Id, shell, StringComparison.OrdinalIgnoreCase));
        var blastKm = spec?.ImpactRadius ?? 0f; // ShellDefinition.ImpactRadius is already km (HE=0.25)
        if (blastKm <= 0.001f)
            return suffix;

        var friendliesInside = new List<string>();
        var friendliesNear = new List<string>();
        var hostilesCovered = new List<string>();
        foreach (var e in _map.ReadEntities())
        {
            if (!e.IsAlive) continue;
            var dx = 10.016f + e.MapX * 3.8164f - kmX;
            var dy = 5.235f + e.MapY * 3.8164f - kmY;
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

        if (friendliesInside.Count > 0 && !allowDanger)
        {
            rejection = $"友军误伤警告 — 已拒绝: {string.Join(", ", friendliesInside)} 在弹着点km({kmX:F2},{kmY:F2})" +
                        $"的{shell}爆炸半径{blastKm * 1000f:F0}m内。用offsetKmX/offsetKmY把弹着点向远离友军一侧移出半径" +
                        "(会牺牲部分毁伤), 或换更小爆炸半径的弹种; 确认接受误伤才用allowDangerouslyFriendlyFire=true重试";
            return suffix;
        }
        if (friendliesInside.Count > 0)
            suffix += $"; 警告: 已确认误伤风险, 友军在爆炸半径内: {string.Join(", ", friendliesInside)}";
        else if (friendliesNear.Count > 0)
            suffix += $"; 注意: 友军贴近弹着点(≤1.5×爆炸半径): {string.Join(", ", friendliesNear)}";
        if (hostilesCovered.Count > 0)
            suffix += $"; 爆炸半径({blastKm * 1000f:F0}m)可同时覆盖: {string.Join(", ", hostilesCovered)}";
        return suffix;
    }

    /// <summary>
    /// LLM-initiated last-minute re-aim of an already-queued/in-preparation FCS task.
    /// Purely fire-and-forget from FCS's perspective: execution never waits for the agent —
    /// no adjustment means the task fires on its original solution; an adjustment is laid
    /// by the FCS staged re-solve pipeline (pre-aim / pre-fire / manual-wait) on its next
    /// pass. Main thread only.
    /// </summary>
    public string AdjustFireMission(AdjustFireRequest req)
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
        else
        {
            return "need target or entityId";
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

        var kmXCheck = 10.016f + mapX * 3.8164f;
        var kmYCheck = 5.235f + mapY * 3.8164f;
        if (!Agent.GridMath.InMapBounds((kmXCheck, kmYCheck)))
            return $"new aim point km({kmXCheck:F1},{kmYCheck:F1}) is outside the map — rejected";

        // Same friendly-fire discipline as fire: the re-aimed burst is surveyed with the
        // task's own shell (looked up from the live FCS set by serial).
        _fcs.TryGetTaskInfo(req.Serial, out var shell, out _);
        var suffix = SurveyBlast(shell, kmXCheck, kmYCheck, req.AllowDangerouslyFriendlyFire, out var ffRejection);
        if (ffRejection != null)
            return ffRejection;

        var result = _fcs.AdjustTaskAim(req.Serial, mapX, mapY);
        if (result.StartsWith("ok"))
        {
            // Keep the impact-matching aim point current; no physical marker is involved
            // (T1/T2 follow automatically via the FCS gun-marker loop).
            if (_deployedTasks.TryGetValue(req.Serial, out var deployed))
                _deployedTasks[req.Serial] = deployed with { Label = label, KmX = kmXCheck, KmY = kmYCheck };
            EventLog.Append("fcs_task_update", "fcs", $"#{req.Serial} 瞄准点已调整 → {label}");
        }
        return result + suffix;
    }

    /// <summary>Pull the bunker signal horn physically. Main thread only.</summary>
    public string PullSignalHorn()
    {
        var horn = SignalOperator.FindHorn(out var candidates);
        if (horn == null)
            return "本关场景中没有找到号角装置(无匹配horn/signal/siren的交互件) — 无法发出信号";
        if (!horn.isActive)
            return $"号角 '{horn.gameObject.name}' 当前不可交互 — 可能尚未满足拉响条件";

        horn.OnClickDown();
        MelonCoroutines.Start(ReleaseHornClick(horn));
        var extra = candidates.Count > 1 ? $" (场景候选: {string.Join(", ", candidates)})" : "";
        EventLog.Append("signal", "game", $"号角已拉响: {horn.gameObject.name}{extra}");
        return $"号角已拉响: {horn.gameObject.name}";
    }

    private static System.Collections.IEnumerator ReleaseHornClick(LookAtTarget horn)
    {
        yield return new UnityEngine.WaitForSeconds(0.15f);
        try { horn.OnClickUp(); } catch { }
    }

    public bool PrintOnTeleprinter(string which, string[] lines)
    {
        var printer = which.Equals("primary", StringComparison.OrdinalIgnoreCase)
            ? Teleprinter.Teleprinters.Primary
            : Teleprinter.Teleprinters.Secondary;
        return _telegraph.Print(printer, lines);
    }
}
