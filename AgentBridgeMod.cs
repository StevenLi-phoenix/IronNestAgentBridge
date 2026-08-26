using Il2CppInterop.Runtime.InteropTypes.Arrays;
using IronNestAgentBridge.Agent;
using IronNestAgentBridge.Fcs;
using IronNestAgentBridge.Fire;
using IronNestAgentBridge.GameState;
using IronNestAgentBridge.Http;
using IronNestAgentBridge.Snapshot;
using IronNestAgentBridge.Ui;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

// MelonGame() without arguments on purpose: the mod is game-agnostic at load time and does its
// own scene probing. Naming a game here would make it refuse to load after a store-page rename.
[assembly: MelonInfo(typeof(IronNestAgentBridge.AgentBridgeMod), "IronNest Agent Bridge", "0.1.0", "stevenli")]
[assembly: MelonGame()]

namespace IronNestAgentBridge;

/// <summary>
/// The mod's only MelonLoader entry point, and deliberately nothing more than one: lifecycle,
/// scene binding, hotkeys, component assembly, and the frame loop that drives every poll.
///
/// All real work lives in the modules this class owns — the map / ammo / teleprinter readers, the
/// FCS gateway, the fire pipeline, the shell ledger, the snapshot builder, the HTTP server and the
/// agent. This class is where they meet the Unity frame and nowhere else.
///
/// Two invariants govern everything here:
/// <list type="bullet">
/// <item><b>Main-thread exclusivity.</b> Every Il2Cpp and Unity touch happens on the OnUpdate
/// stack. Background threads reach the game only through <see cref="MainThread"/>, which is
/// pumped once per frame ahead of this class's own work. Consequently every public operation
/// method below is a main-thread-only contract.</item>
/// <item><b>No exception may leave a Melon callback.</b> One uncaught throw and MelonLoader
/// unloads the whole mod mid-mission, so each poll block carries its own guard: the five 0.5 s
/// checks are guarded individually, because a single game singleton going missing must not take
/// the world clock and the counter-battery relay down with it.</item>
/// </list>
/// </summary>
public sealed class AgentBridgeMod : MelonMod
{
    // ---------------------------------------------------------------- cross-thread state

    /// <summary>
    /// Mirrors <c>Application.isFocused</c>. The agent thread pauses on it, mirroring FCS's own
    /// focus gate: while the game is in the background FCS suspends its automation too, so a
    /// decision taken now would be both meaningless and paid for in tokens.
    /// </summary>
    public static volatile bool GameFocused = true;

    /// <summary>
    /// A cutscene is playing. The agent pauses and the panel hides — a cutscene can start
    /// mid-mission and the world it shows is not the world the guns are in.
    /// </summary>
    public static volatile bool CinematicActive;

    /// <summary>
    /// Mission clock in seconds, the time base a motion model is anchored to. Written by the
    /// world-clock poll, read by the fire pipeline on the agent's thread.
    /// </summary>
    public static volatile float MissionClockSeconds;

    /// <summary>
    /// True only while the in-game 24 h world clock is the source of
    /// <see cref="MissionClockSeconds"/>. Missions that fall back to the mission stopwatch have
    /// no shared absolute axis, so an "at HH:mm" observation cannot be anchored on them at all.
    /// </summary>
    public static volatile bool WorldClockAvailable;

    // ---------------------------------------------------------------- components

    private readonly MapReader _map = new();
    private readonly ImpactReader _impacts = new();
    private readonly TeleprinterReader _teleprinters = new();
    private readonly FcsGateway _fcs = new();
    private readonly ShellTracker _shells = new();
    private readonly PollScheduler _ticks = new();

    private readonly FireMissionPipeline _fire;
    private readonly SnapshotBuilder _snapshots;

    private FdoAgent? _agent;
    private BridgeServer? _http;

    public AgentBridgeMod()
    {
        _fire = new FireMissionPipeline(_map, _fcs, _shells);
        _snapshots = new SnapshotBuilder(_map, _fcs, _shells, _teleprinters);
    }

    // ---------------------------------------------------------------- mission-scoped state

    /// <summary>FCS summary text for the panel; opaque to everyone but the panel.</summary>
    public string LastFcsSummary { get; private set; } = "";

    /// <summary>
    /// A behaviour flag, not a property of the piece's position: true only once somebody has
    /// actually placed the piece this mission — the agent's tool, or a hand the manual detector
    /// caught. It can never be inferred from coordinates, because the un-placed piece sits on a
    /// perfectly plausible-looking default.
    /// </summary>
    public bool TurretCalibrated { get; private set; }

    /// <summary>Baseline game camera; a different one (or none) means a cutscene is running.</summary>
    private Camera? _baselineCamera;
    private int _baselineCameraId;

    private Il2Cpp.GenericTimerSceneSync? _worldClock;

    /// <summary>
    /// The candidate inventory is a one-shot diagnostic. Without this latch a scene whose only
    /// timers read zero would re-enumerate and re-log twice a second forever.
    /// </summary>
    private bool _worldClockInventoryLogged;

    private Il2Cpp.MissionManager.GamePhase? _previousPhase;

    private bool _counterBatteryRunning;

    /// <summary>De-duplication key for the FCS card receipt, which is a latched value.</summary>
    private string? _lastCardResult;

    private Vector3? _lastPieceLocal;
    private (float x, float y)? _lastReportedTurretKm;
    private bool _manualMovePending;
    private float _manualMoveSettleAt;

    // ---------------------------------------------------------------- constants

    /// <summary>Map-local movement above which the piece counts as having been dragged.</summary>
    private const float ManualMoveEpsilonLocal = 0.02f;

    /// <summary>How long the piece must sit still before a drag is reported as finished.</summary>
    private const float ManualSettleSeconds = 2f;

    /// <summary>Re-reports below this are noise from a hand resting on the piece. Kilometres.</summary>
    private const float ManualReportMinDeltaKm = 0.2f;

    /// <summary>
    /// The km-frame origin. It is also the value the snapshot shows for an un-placed piece, which
    /// is precisely why declaring it as a turret position has to be refused.
    /// </summary>
    private const float OriginSentinelToleranceKm = 0.15f;

    // =======================================================================================
    // MelonMod lifecycle
    // =======================================================================================

    public override void OnInitializeMelon()
    {
        // First, unconditionally: everything below reads configuration.
        AgentConfig.Initialize();

        // Lazy by contract — FCS may not have loaded yet, and resolving now would cache a null
        // for the rest of the process.
        RequisitionOperator.RequisitionLockProvider = () => _fcs.GetRequisitionLock();

        // Constructed, never started. Fire control is granted by hand, once per mission, with F11.
        _agent = new FdoAgent(this);

        if (!AgentConfig.EnableHttpApi)
        {
            MelonLogger.Msg("[AgentBridge] HTTP API disabled (EnableHttpApi=false)");
            return;
        }

        try
        {
            _http = new BridgeServer(this);
            _http.Start();
        }
        catch (Exception ex)
        {
            // A taken port must cost the debug API, not the mod.
            _http = null;
            MelonLogger.Error($"[AgentBridge] failed to start HTTP server on port {BridgeServer.Port}: {ex.Message}");
        }
    }

    public override void OnDeinitializeMelon()
    {
        try { _agent?.Stop(); } catch { }
        try { _http?.Stop(); } catch { }
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        // Every scene reference we hold is now dangling. Rebinding is cheap; acting on a stale
        // transform is not.
        _map.Unbind();
        Agent.GridMath.ResetMapBounds();
        _teleprinters.Reset();

        _baselineCamera = null;
        _baselineCameraId = 0;
        _worldClock = null;
        _worldClockInventoryLogged = false;

        // Queued work was written against the scene that just went away.
        MainThread.Clear();

        _ticks.Bind.ScheduleIn(Il2CppSafe.Get(() => Time.realtimeSinceStartup, 0f), MapReader.BindRetrySeconds);
    }

    public override void OnGUI()
    {
        // Mirrors FCS: no HUD until the tactical map is bound, plus a cutscene gate of our own.
        if (_agent == null || !_map.IsBound || CinematicActive) return;

        AgentWindow.Draw(_agent, this);
    }

    public override void OnUpdate()
    {
        // Fixed opening, in this order: the focus mirror the agent reads, then the pump that lets
        // every background caller reach the game before this frame's own work runs.
        GameFocused = Il2CppSafe.Get(() => Application.isFocused, true);
        MainThread.Pump();

        // Sampled exactly once and shared by every beat; see PollScheduler.
        var now = Il2CppSafe.Get(() => Time.realtimeSinceStartup, 0f);

        if (!_map.IsBound && _ticks.Bind.Due(now)) TryBindMap();

        if (_map.IsBound && _ticks.Map.Due(now)) MapTick(now);
        if (_ticks.Telegraph.Due(now)) TelegraphTick();

        PollHotkeys();

        if (_ticks.Misc.Due(now)) MiscTick(now);
        if (_ticks.Fcs.Due(now)) FcsTick();
    }

    // =======================================================================================
    // Poll beats
    // =======================================================================================

    private void TryBindMap()
    {
        try
        {
            if (!_map.TryBind()) return;
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[AgentBridge] map bind failed: {ex.Message}");
            return;
        }

        // The firing envelope for this mission. Without a measured sheet the generous fallback
        // stands, which lets a wild aim point through rather than refusing a legitimate one.
        if (_map.KmBounds is { } bounds)
        {
            Agent.GridMath.SetMapBoundsKm(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
            MelonLogger.Msg(
                $"[AgentBridge] tactical map bound; sheet extent km({bounds.MinX:F1},{bounds.MinY:F1})-({bounds.MaxX:F1},{bounds.MaxY:F1})");
        }
        else
        {
            Agent.GridMath.ResetMapBounds();
            MelonLogger.Msg("[AgentBridge] tactical map bound; sheet unmeasured — generous bounds fallback");
        }
    }

    private void MapTick(float now)
    {
        try { _map.PollAndEmitEvents(); }
        catch (Exception ex) { MelonLogger.Warning($"[AgentBridge] map poll failed: {ex.Message}"); }

        // Silent by design: impact markers churn during scene transitions and a warning per
        // half-second would bury everything else in the log.
        try { _impacts.PollAndEmitEvents(_map.MapSurface, _shells.OnShellImpact); }
        catch { }

        try { _shells.ResolveOverdueShells(); }
        catch { }

        try { _shells.PollFriendlyIntrusions(now, _map); }
        catch { }
    }

    private void TelegraphTick()
    {
        // Deliberately not gated on a bound map: dispatches arrive whether or not we found the
        // command table.
        try { _teleprinters.PollAndEmitEvents(); }
        catch (Exception ex) { MelonLogger.Warning($"[AgentBridge] telegraph poll failed: {ex.Message}"); }
    }

    /// <summary>
    /// The five half-second checks. Each is guarded on its own: they read unrelated game
    /// singletons and any of them may be absent in a given scene.
    /// </summary>
    private void MiscTick(float now)
    {
        try { UpdateCinematic(); } catch { }
        try { DetectManualCalibration(now); } catch { }
        try { UpdateMissionPhase(); } catch { }
        try { UpdateCounterBattery(now); } catch { }
        try { UpdateWorldClock(); } catch { }
    }

    private void FcsTick()
    {
        try
        {
            var status = _fcs.ReadStatus();
            LastFcsSummary = FormatFcsSummary(status);

            _shells.TrackFiredShells(status);

            // The receipt is a latched field, so it reads the same on every poll until the next
            // purchase overwrites it; only a change is news.
            var cardResult = _fcs.ReadConsoleCardResult();
            if (!string.IsNullOrEmpty(cardResult) && cardResult != _lastCardResult)
            {
                _lastCardResult = cardResult;
                EventLog.Append("requisition", "fcs",
                    $"card request completed: {cardResult}{ShellTracker.BalanceSuffix()}");
                TransactionLog.Write("requisition", cardResult!);
            }
        }
        catch { }
    }

    private void PollHotkeys()
    {
        try
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.f10Key.wasPressedThisFrame) AgentWindow.Visible = !AgentWindow.Visible;
            if (keyboard.f11Key.wasPressedThisFrame) ToggleLlmControl();

            // Same key and same meaning as the FCS plan reset: one gesture wipes both sides.
            if (keyboard.f9Key.wasPressedThisFrame) FullReset("F9");
        }
        catch { }
    }

    /// <summary>Panel text. T9 and T10 are the fixed gun-position labels, left and right.</summary>
    private static string FormatFcsSummary(FcsStatusDto status)
    {
        var text = $"FCS: pending={status.PendingCount} done={status.CompletedTaskCount} fail={status.FailedTaskCount}";
        if (status.LeftTask != null) text += $"\nT9(左): {status.LeftTask}";
        if (status.RightTask != null) text += $"\nT10(右): {status.RightTask}";
        return text;
    }

    // =======================================================================================
    // Cinematic detection
    // =======================================================================================

    /// <summary>
    /// A cutscene always switches cameras, so the test is identity of <c>Camera.main</c> against
    /// the baseline captured at bind time. Instance ids are compared rather than the wrappers:
    /// Unity's <c>==</c> reports a destroyed object as null, which would read as "cutscene" for
    /// every torn-down camera.
    /// </summary>
    private void UpdateCinematic()
    {
        Camera? cam;
        try { cam = Camera.main; }
        catch { cam = null; }

        if (_baselineCamera == null)
        {
            // Only capture from a bound scene: the menu camera is not the mission's baseline.
            if (_map.IsBound && cam != null)
            {
                _baselineCamera = cam;
                _baselineCameraId = Il2CppSafe.Get(() => cam!.GetInstanceID(), 0);
            }

            // Without a baseline there is nothing to compare against, so nothing may be claimed.
            SetCinematic(false, cam);
            return;
        }

        var active = cam == null || Il2CppSafe.Get(() => cam!.GetInstanceID(), 0) != _baselineCameraId;
        SetCinematic(active, cam);
    }

    private void SetCinematic(bool active, Camera? cam)
    {
        if (active == CinematicActive) return;

        CinematicActive = active;

        var name = cam == null ? "none" : Il2CppSafe.Get(() => cam!.name, "none");
        MelonLogger.Msg($"[AgentBridge] cinematic {(active ? "started" : "ended")} (main camera: {name})");
        EventLog.Append("cinematic", "game", active ? "cinematic started" : "cinematic ended");
    }

    // =======================================================================================
    // World clock
    // =======================================================================================

    /// <summary>
    /// Mirrors the in-game 24 h world clock, and only falls back to the mission stopwatch when
    /// there is none. The distinction matters beyond formatting: the world clock is the shared
    /// axis that dispatch timestamps, events and motion observations are all quoted against,
    /// while the stopwatch is local to the run and cannot anchor an absolute observation.
    /// </summary>
    private void UpdateWorldClock()
    {
        try
        {
            _worldClock ??= FindWorldClock(ref _worldClockInventoryLogged);

            if (_worldClock != null)
            {
                var seconds = _worldClock.CurrentTime;
                if (seconds > 0f)
                {
                    MissionClockSeconds = seconds;
                    WorldClockAvailable = true;
                    EventLog.GameClock = $"{(int)(seconds / 3600f) % 24:00}:{(int)(seconds / 60f) % 60:00}";
                    return;
                }

                // A clock that stopped reading is not this scene's clock; search again next tick.
                _worldClock = null;
            }
        }
        catch
        {
            _worldClock = null;
        }

        FallBackToStopwatch();
    }

    /// <summary>
    /// The scene may hold several timers (a pocket watch, a wall clock). The one furthest along
    /// is the world clock; the others are props or countdowns that started later.
    /// </summary>
    private static Il2Cpp.GenericTimerSceneSync? FindWorldClock(ref bool inventoryLogged)
    {
        Il2CppArrayBase<Il2Cpp.GenericTimerSceneSync>? timers;
        try { timers = UnityEngine.Object.FindObjectsOfType<Il2Cpp.GenericTimerSceneSync>(); }
        catch { return null; }

        if (timers == null) return null;

        Il2CppArrayBase<Il2Cpp.GenericTimerSceneSync> found = timers;
        var log = !inventoryLogged;

        Il2Cpp.GenericTimerSceneSync? best = null;
        var bestTime = float.MinValue;

        var count = Il2CppSafe.Get(() => found.Length, 0);
        for (var i = 0; i < count; i++)
        {
            var index = i;
            var timer = Il2CppSafe.GetRef(() => found[index]);
            if (timer == null) continue;

            var seconds = Il2CppSafe.Get(() => timer!.CurrentTime, 0f);

            if (log)
            {
                // Boxed: the timer id's declared type differs between game builds, and the log
                // line only ever needs its text.
                var id = Il2CppSafe.Get<object?>(() => timer!.TimerID, null)?.ToString() ?? "?";
                MelonLogger.Msg($"[AgentBridge] world clock candidate '{id}' t={seconds:F0}s");
                inventoryLogged = true;
            }

            if (seconds <= bestTime) continue;
            bestTime = seconds;
            best = timer;
        }

        return best;
    }

    /// <summary>Mission stopwatch, "mm:ss". No absolute axis, hence no world-clock claim.</summary>
    private static void FallBackToStopwatch()
    {
        WorldClockAvailable = false;

        try
        {
            var stats = Il2Cpp.MissionStatsTracker.Instance;
            if (stats == null || !stats.timerRunning) return;

            var seconds = stats.timerValue;
            MissionClockSeconds = seconds;
            EventLog.GameClock = $"{(int)(seconds / 60f):00}:{(int)(seconds % 60f):00}";
        }
        catch { }
    }

    // =======================================================================================
    // Mission phase
    // =======================================================================================

    /// <summary>
    /// Ties the agent's session to the mission's. Leaving the active phase stops it; entering one
    /// wipes the previous conversation. It never starts the agent: fire control is opt-in, once
    /// per mission, and an automatic start would put an LLM on the guns without anyone asking.
    /// </summary>
    private void UpdateMissionPhase()
    {
        Il2Cpp.MissionManager.GamePhase phase;
        try
        {
            var manager = Il2Cpp.MissionManager.Instance;
            if (manager == null) return;
            phase = manager.CurrentPhase;
        }
        catch
        {
            return;
        }

        var previous = _previousPhase;
        _previousPhase = phase;

        // The very first sample is a reading, not a transition: treating it as one would fire a
        // reset the instant the mod loads.
        if (previous == null || previous.Value == phase) return;

        var active = Il2Cpp.MissionManager.GamePhase.MissionActive;

        if (previous.Value == active)
        {
            MelonLogger.Msg($"[AgentBridge] mission ended ({previous.Value}->{phase}) — agent auto-stop");
            TransactionLog.Write("mission", $"mission ended ({previous.Value}->{phase}); agent auto-stopped");

            if (AgentConfig.LlmControl) AgentConfig.LlmControl = false;
            if (_agent is { IsRunning: true }) _agent.Stop();
            return;
        }

        if (phase == active) FullReset("new mission — clearing previous conversation");
    }

    // =======================================================================================
    // Counter-battery relay
    // =======================================================================================

    private void UpdateCounterBattery(float now)
    {
        Il2Cpp.CounterBatteryTimer? timer;
        try { timer = Il2Cpp.CounterBatteryTimer.Instance; }
        catch { _counterBatteryRunning = false; return; }

        if (timer == null)
        {
            _counterBatteryRunning = false;
            return;
        }

        bool running, expired, permanentlyStopped;
        float remaining;
        try
        {
            running = timer.IsRunning;
            expired = timer.IsExpired;
            permanentlyStopped = timer.IsPermanentlyStopped;
            remaining = timer.TimeRemaining;
        }
        catch
        {
            // A partial read is worse than none: keep the state and try again in half a second.
            return;
        }

        if (permanentlyStopped)
        {
            if (_counterBatteryRunning)
            {
                _counterBatteryRunning = false;
                EventLog.Append("counter_battery", "game", "反炮击倒计时已永久解除 — 威胁排除");
            }
            return;
        }

        if (expired)
        {
            if (_counterBatteryRunning)
            {
                _counterBatteryRunning = false;
                EventLog.Append("counter_battery", "game", "反炮击倒计时归零 — 敌炮火正在覆盖本阵地");
            }
            return;
        }

        if (!running)
        {
            _counterBatteryRunning = false;
            return;
        }

        if (!_counterBatteryRunning)
        {
            _counterBatteryRunning = true;
            _ticks.CounterBattery.ScheduleIn(now, PollScheduler.CounterBatteryBroadcastSeconds);
            EventLog.Append("counter_battery", "game",
                $"反炮击倒计时启动: 剩余 {FormatCountdown(remaining)} — 归零时敌炮火覆盖本阵地");
            return;
        }

        if (!_ticks.CounterBattery.IsDue(now)) return;

        _ticks.CounterBattery.ScheduleIn(now, PollScheduler.CounterBatteryBroadcastSeconds);
        EventLog.Append("counter_battery", "game", $"反炮击倒计时: 剩余 {FormatCountdown(remaining)}");
    }

    private static string FormatCountdown(float seconds)
        => $"{(int)(seconds / 60f):00}:{(int)(seconds % 60f):00}";

    // =======================================================================================
    // Turret calibration
    // =======================================================================================

    /// <summary>
    /// Watches the draggable turret piece for the commander's own hand.
    ///
    /// The first placement of a mission is announced as soon as it is seen — that is the moment
    /// the assumed position stops being a default. Later moves are announced only once the piece
    /// has settled and only when it actually went somewhere, so dragging it across the table
    /// produces one event at the destination instead of a stream of them along the way.
    /// </summary>
    private void DetectManualCalibration(float now)
    {
        if (!_map.IsBound) return;

        var local = _map.TurretLocalOnMap();
        var previous = _lastPieceLocal;
        _lastPieceLocal = local;

        var moved = previous != null
                    && (MathF.Abs(local.x - previous.Value.x) > ManualMoveEpsilonLocal
                        || MathF.Abs(local.y - previous.Value.y) > ManualMoveEpsilonLocal);

        if (moved)
        {
            _manualMovePending = true;
            _manualMoveSettleAt = now + ManualSettleSeconds;

            if (!TurretCalibrated)
            {
                TurretCalibrated = true;
                _manualMovePending = false;
                _lastReportedTurretKm = MapFrame.LocalToKm(local.x, local.y);
                EventLog.Append("turret_position", "map",
                    "turret piece was moved manually — treated as calibrated");
            }

            return;
        }

        if (!_manualMovePending || now < _manualMoveSettleAt) return;
        _manualMovePending = false;

        var km = MapFrame.LocalToKm(local.x, local.y);
        if (_lastReportedTurretKm is { } last)
        {
            var dx = km.x - last.x;
            var dy = km.y - last.y;
            if (MathF.Sqrt(dx * dx + dy * dy) <= ManualReportMinDeltaKm) return;
        }

        _lastReportedTurretKm = km;

        // No coordinates in the text: where the gun stands is something the agent establishes
        // itself, through the dedicated tool. This only tells it that its cached bearings and
        // ranges are now stale.
        EventLog.Append("turret_position", "map",
            "炮塔棋子被再次手动移动并已稳定 — 假定炮位已变更; 用get_assumed_turret_position复核后重新解算既有目标");
    }

    /// <summary>
    /// Declares where the turret is BELIEVED to stand. Main thread only.
    /// </summary>
    public string SetDeclaredTurret(float kmX, float kmY)
    {
        if (!Agent.GridMath.InMapBounds((kmX, kmY)))
            return $"km({kmX:F1},{kmY:F1}) is outside the map — rejected (check the grid conversion)";

        // The origin sentinel: this exact point is what an unplaced piece reads as, so a model
        // that saw it in a receipt and echoed it back is quoting the placeholder, never a fix.
        if (MathF.Abs(kmX - MapFrame.MapOffsetX) < OriginSentinelToleranceKm
            && MathF.Abs(kmY - MapFrame.MapOffsetY) < OriginSentinelToleranceKm)
        {
            return "km(10.02,5.24) 是地图原点(未校准哨兵值), 不是真实炮位 — rejected。校准依据只能是统帅部电文里的铁巢网格";
        }

        var (ok, message) = _map.SetDeclaredTurret(kmX, kmY);

        if (ok)
        {
            TurretCalibrated = true;

            // Refresh the drag detector's baseline, or the move we just made would come back a
            // moment later as a manual calibration.
            _lastPieceLocal = _map.TurretLocalOnMap();
            _lastReportedTurretKm = (kmX, kmY);
            _manualMovePending = false;
        }

        EventLog.Append("turret_position", "map", message);
        return message;
    }

    // =======================================================================================
    // Public operations (main thread only — callers marshal through MainThread.Run)
    // =======================================================================================

    public StateSnapshotDto BuildSnapshot() => _snapshots.Build(TurretCalibrated);

    public string QueueFireMission(FireMissionRequest req) => _fire.QueueFireMission(req);

    public string AdjustFireMission(AdjustFireRequest req) => _fire.AdjustFireMission(req);

    /// <summary>
    /// Cancels a queued task. The ledger entry is dropped as well when FCS accepted: FCS now
    /// records a cancellation in its outcomes, so the fired/failed reconciliation would catch it
    /// anyway, but a task that is definitely gone must not linger here either. A refused cancel
    /// leaves the ledger alone — the task is still live and still ours to track.
    /// </summary>
    public string CancelPendingFcsTask(int serial)
    {
        var result = _fcs.CancelPending(serial);
        if (result.StartsWith("ok", StringComparison.Ordinal)) _shells.Forget(serial);

        EventLog.Append("fcs_task_update", "fcs", $"cancel #{serial}: {result}");
        return result;
    }

    /// <summary>
    /// Buys a punch card. The budget gate applies to special cards only and only when both the
    /// price and the balance can actually be read — an unreadable figure lets the purchase
    /// through, because refusing on a guess costs a mission-critical card.
    /// </summary>
    public string RequestCard(
        string cardId,
        float? bearingDeg,
        int priority = 50,
        string? startGrid = null,
        float? distanceKm = null)
    {
        var balance = AmmoReader.ReadRequisitionPoints();
        if (balance.HasValue)
        {
            foreach (var card in AmmoReader.ReadCards())
            {
                if (!string.Equals(card.Id, cardId, StringComparison.OrdinalIgnoreCase)) continue;

                if (card.Cost > 0 && card.Cost > balance.Value)
                    return $"征用点不足: {cardId} 需{card.Cost}点, 余额仅{balance.Value}点 — rejected";

                break;
            }
        }

        // Preferred path: FCS's own console coordinator, which owns the priority queue and can
        // preempt a low-priority purchase with an urgent one.
        var viaFcs = _fcs.RequestCardPurchase(cardId, bearingDeg, priority, startGrid, distanceKm);
        if (viaFcs.Accepted)
        {
            EventLog.Append("requisition", "fcs", $"card '{cardId}' {viaFcs.Message}");
            return viaFcs.Message + " (result arrives via events)";
        }

        // Fallback: drive the console by hand. It has no distance dial support, so a card that
        // needs one (MoveDirection) cannot be bought this way and the parameter is dropped.
        return RequisitionOperator.StartPurchase(cardId, bearingDeg, null);
    }

    public string PullSignalHorn() => SignalOperator.Sound();

    /// <summary>
    /// Prints on a teleprinter. Anything that is not "primary" goes to the battlefield-report
    /// machine, typos included: a misrouted line is recoverable, a refused one is lost.
    /// </summary>
    public bool PrintOnTeleprinter(string which, string[] lines) => TeleprinterReader.Print(which, lines);

    public Vector3 ReadTurretLocal() => _map.TurretLocalOnMap();

    /// <summary>Visible entities only — an entity in the fog must never reach the LLM.</summary>
    public MapEntityDto? FindVisibleEntity(string entityId) => _map.FindEntity(entityId);

    public List<string> DescribeInFlight() => _shells.DescribeInFlight();

    // =======================================================================================
    // Master switch and full reset
    // =======================================================================================

    /// <summary>F11 and the panel button. The only way an agent ever starts.</summary>
    public void ToggleLlmControl()
    {
        var on = !AgentConfig.LlmControl;
        AgentConfig.LlmControl = on;

        MelonLogger.Msg($"[AgentBridge] LLM control {(on ? "ON" : "OFF")}");

        if (_agent == null) return;

        if (on && !_agent.IsRunning) _agent.Start();
        else if (!on && _agent.IsRunning) _agent.Stop();
    }

    /// <summary>
    /// F9 semantics: everything the bridge believes about this mission is discarded.
    ///
    /// It stops the agent and never restarts it. Fire control is granted by hand and a reset is
    /// exactly the moment when the previous grant stopped being informed consent — F11 is the
    /// one opt-in.
    /// </summary>
    public void FullReset(string reason)
    {
        MelonLogger.Msg($"[AgentBridge] full reset ({reason})");
        TransactionLog.Write("reset", $"full reset: {reason}");

        // Before anything else: queued closures were written against the world we are about to
        // discard, and one of them could really fire a gun.
        MainThread.Clear();

        _agent?.Stop();
        _agent?.ClearLog();

        // Stale events must never be replayed into a restarted agent's fresh context.
        EventLog.Clear();

        _lastCardResult = null;
        _shells.Clear();

        _map.Unbind();
        Agent.GridMath.ResetMapBounds();
        _impacts.Reset();
        _teleprinters.Reset();

        _baselineCamera = null;
        _baselineCameraId = 0;
        _worldClock = null;
        _worldClockInventoryLogged = false;
        _counterBatteryRunning = false;

        TurretCalibrated = false;
        _lastPieceLocal = null;
        _lastReportedTurretKm = null;
        _manualMovePending = false;

        LastFcsSummary = "";

        // Shorter than the routine retry: a reset is deliberate and the map is expected back at once.
        _ticks.ResetAll();
        _ticks.Bind.ScheduleIn(
            Il2CppSafe.Get(() => Time.realtimeSinceStartup, 0f),
            PollScheduler.RebindAfterResetSeconds);
    }
}
