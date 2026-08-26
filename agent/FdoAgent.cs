using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using IronNestAgentBridge.GameState;
using UnityEngine;

namespace IronNestAgentBridge.Agent;

/// <summary>
/// The fire-direction officer: an event-driven, multi-round LLM conversation running on its own
/// background thread, whose decisions reach the guns exclusively through tool calls.
///
/// This class touches no game object. Every read and every action is marshalled through
/// <see cref="MainThread"/> — synchronously with a timeout when the answer is needed, or
/// fire-and-forget when it is purely decorative. The one thing it owns outright is the
/// conversation.
///
/// The conversation is append-only and byte-stable by design. System message first and unchanged,
/// every assistant and tool turn left exactly where it landed: that is what keeps the provider's
/// prefix cache hitting, and a cache miss on a context this size is the single most expensive
/// mistake available here. Only <see cref="CompactConversation"/> may rebuild the history.
/// </summary>
public sealed class FdoAgent
{
    // ---------------------------------------------------------------- loop tuning

    /// <summary>Long-poll slice while waiting for events. Also the unit of the idle back-off.</summary>
    private const int PollSliceMs = 5000;

    /// <summary>
    /// Idle slices before a synthetic re-check. 12 × 5 s = 60 s at the shortest interval; the
    /// back-off then doubles it up to a ceiling.
    /// </summary>
    private const int RecheckAfterSlices = 12;

    /// <summary>Debounce slice: a burst is still arriving as long as this keeps returning events.</summary>
    private const int DebounceSliceMs = 1000;

    /// <summary>Hard ceiling on the debounce window, so a chatty battlefield cannot starve a decision.</summary>
    private const int DebounceWindowMs = 6000;

    /// <summary>Back-off after an unexpected error, before the loop tries again.</summary>
    private const int ErrorBackoffMs = 5000;

    /// <summary>Pause-gate slice while the game is unfocused or a cutscene is running.</summary>
    private const int PauseSliceMs = 1000;

    /// <summary>
    /// Events injected into a single round. A long spell unfocused lets the backlog grow without
    /// bound (the cursor does not advance while paused), and the whole backlog in one prompt is
    /// both unreadable and expensive. The oldest are folded into a single line instead.
    /// </summary>
    private const int MaxEventsPerRound = 60;

    /// <summary>Decision text kept in <see cref="LastReason"/>; the rest is for the transcript.</summary>
    private const int MaxReasonChars = 500;

    /// <summary>Raw tool arguments kept in the "recent calls" line.</summary>
    private const int MaxToolArgsChars = 120;

    private const int MaxRecentToolCalls = 20;
    private const int MaxLogEntries = 300;

    // ---------------------------------------------------------------- compaction
    //
    // Three numbers meet here and are easy to confuse:
    //
    //   * CompactAtPromptTokens (400k) is a PROMPT-side threshold. It is compared against
    //     UsageMeter.LastPromptTokens, which is the previous round's figure — the current
    //     round's prompt has not been sent yet, so the trigger is inherently one round late.
    //     That lag is acceptable: the gap between 400k and the provider's context ceiling is far
    //     wider than one round of growth.
    //   * The `_messages.Count > 3` guard means "at least one full exchange has happened".
    //     Compacting a conversation of system + user would summarise nothing and cost a call.
    //   * AgentConfig.MaxTokens (393216) is the OUTPUT cap sent with every request. It is
    //     unrelated to this threshold and the two must never be reconciled with each other.

    private const long CompactAtPromptTokens = 400_000;

    /// <summary>Minimum history length before compaction is worth its own API call.</summary>
    private const int CompactMinMessages = 3;

    /// <summary>Verbatim handover-briefing prompt. One line; the numbered clauses are the contract.</summary>
    private const string CompactPrompt =
        "请把到目前为止的战况压缩成一份接班简报, 只输出简报文本: 1)已确认摧毁的目标 2)已下达但未确认结果的任务 " +
        "3)存活/待处理目标与其弹种方案 4)观测员/参考点网格等长期情报 5)已学到的弹药与精度教训 6)统帅部的有效指令与限制";

    // ---------------------------------------------------------------- main-thread timeouts

    /// <summary>Reads: cheap, and a slow one usually means the frame loop is blocked anyway.</summary>
    private const int ReadTimeoutMs = 10_000;

    /// <summary>Writes and the snapshot: they walk the scene graph and talk to FCS.</summary>
    private const int WriteTimeoutMs = 15_000;

    // ---------------------------------------------------------------- serialisation

    /// <summary>
    /// The relaxed encoder is mandatory: every refusal and every receipt is Chinese, and the
    /// default encoder would turn them into \uXXXX escapes inside the model's own context.
    /// </summary>
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Ammunition ids, as opposed to special punch cards. PLCM and PCLM are both listed on
    /// purpose: the game asset is PCLM while the upstream enum spells it PLCM, and a card that
    /// falls out of this set would be offered to the model as a "special card" it cannot fire.
    /// </summary>
    private static readonly HashSet<string> ShellIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "AP", "APHE", "ATMC", "CLMN", "CYAN", "DRIL", "EQKE", "FLCH", "HCHE", "HE", "INCN",
        "LE", "PLCM", "PCLM", "PHGN", "PRPG", "SMK", "STAR", "TEAR", "THRM", "WP",
    };

    // ---------------------------------------------------------------- state

    public enum AgentState
    {
        Stopped,
        Running,
        Paused,

        /// <summary>Stop requested; the thread is finishing the round it is already in.</summary>
        Stopping,
    }

    private readonly AgentBridgeMod _mod;

    /// <summary>Guards the three display collections. Never held across an API call.</summary>
    private readonly object _gate = new();

    private readonly List<string> _log = new();
    private readonly List<string> _recentToolCalls = new();

    /// <summary>The conversation. Owned here, appended to in place by <see cref="LlmClient"/>.</summary>
    private readonly List<object> _messages = new();

    // Scalars read by the Unity main thread every frame and written by the agent thread. They are
    // unlocked on purpose, so each one must be a single assignment of an immutable value.
    private volatile int _state = (int)AgentState.Stopped;
    private volatile string _status = "stopped";
    private volatile string _lastReason = "";
    private volatile string _streamingText = "";
    private volatile bool _isStreaming;
    private volatile Thread? _thread;

    private CancellationTokenSource? _cts;

    /// <summary>Handover briefing waiting to be injected into the next round, exactly once.</summary>
    private string _carrySummary = "";

    /// <summary>
    /// Agent-thread private. Advanced in two places — the main loop's pick-up and the tool
    /// exit's ride-along — which are mutually exclusive on this one thread and therefore need no
    /// lock. Nothing outside this thread may touch it.
    /// </summary>
    private long _eventCursor;

    /// <summary>Consecutive synthetic re-checks; drives the exponential idle back-off.</summary>
    private int _idleRechecks;

    /// <summary>Missions FCS accepted this round. Only accepted ones — an attempt is not an action.</summary>
    private int _firesThisRound;

    /// <summary>
    /// The round's turret origin in km, frozen at the start of the round so every coordinate the
    /// model sees within one decision resolves against the same point. Refreshed mid-round only
    /// by a successful <c>set_assumed_turret_position</c>, which is precisely the case where
    /// keeping the old origin would silently answer the model's follow-up questions from a
    /// position it just corrected.
    /// </summary>
    private (float x, float y) _turretKm;

    /// <summary>The round's snapshot, consumed by the pure-maths tools. Agent thread only.</summary>
    private StateSnapshotDto? _snapshot;

    public FdoAgent(AgentBridgeMod mod) => _mod = mod;

    // ---------------------------------------------------------------- public surface

    public AgentState State => (AgentState)_state;
    public string Status => _status;
    public string LastReason => _lastReason;
    public string StreamingText => _streamingText;
    public bool IsStreaming => _isStreaming;

    public bool IsRunning => _thread is { IsAlive: true };

    /// <summary>
    /// Opens a brand-new session. Nothing is inherited: not the history, not the handover
    /// briefing, not the idle back-off, not the usage meter. A restart is a new commander taking
    /// the chair, and the state of the last one is exactly what must not colour this one.
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        if (!AgentConfig.LlmControl)
        {
            _status = "LLM control disabled";
            return;
        }

        if (string.IsNullOrWhiteSpace(AgentConfig.ApiKey))
        {
            _status = @"no ApiKey — set [AgentBridge] ApiKey in UserData\MelonPreferences.cfg";
            return;
        }

        try { _cts?.Dispose(); }
        catch { }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _messages.Clear();
        _messages.Add(SystemMessage());
        _carrySummary = "";
        _idleRechecks = 0;

        // Metering is per session, so the panel's cost figure answers "this conversation".
        UsageMeter.Reset();

        var thread = new Thread(() => Loop(ct))
        {
            IsBackground = true,
            Name = "AgentBridge-FDO",
        };
        _thread = thread;

        // Set before the thread runs: if it exits immediately its finally would otherwise be
        // overwritten by a Running that never was.
        _state = (int)AgentState.Running;
        _status = "running";

        thread.Start();
        AppendLog("agent started");
    }

    /// <summary>
    /// Requests a stop and returns at once. The thread may be mid-round inside a streaming HTTP
    /// call; it settles its own state in its finally. Joining here would freeze the frame loop
    /// for as long as the provider takes to answer.
    /// </summary>
    public void Stop()
    {
        try { _cts?.Cancel(); }
        catch { }

        var running = IsRunning;
        _state = (int)(running ? AgentState.Stopping : AgentState.Stopped);
        _status = running ? "stopping (finishing current round)" : "stopped";

        AppendLog("agent stop requested");
    }

    /// <summary>
    /// Clears the display state only. The conversation is untouched — it is rebuilt by
    /// <see cref="Start"/> and by nothing else.
    /// </summary>
    public void ClearLog()
    {
        lock (_gate)
        {
            _log.Clear();
            _recentToolCalls.Clear();
        }

        _streamingText = "";
        _lastReason = "";
    }

    /// <summary>Copy; the UI thread must never enumerate a live list.</summary>
    public IReadOnlyList<string> LogSnapshot()
    {
        lock (_gate) return _log.ToList();
    }

    /// <summary>Copy; the UI thread must never enumerate a live list.</summary>
    public List<string> RecentToolCalls()
    {
        lock (_gate) return _recentToolCalls.ToList();
    }

    // =======================================================================================
    // Main loop
    // =======================================================================================

    private void Loop(CancellationToken ct)
    {
        _eventCursor = 0;
        var idleSlices = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // ---- pause gate, first thing every round ---------------------------------
                    // Mirrors the FCS focus gate: with the game in the background FCS suspends
                    // its own automation, so a decision now would be taken against a frozen
                    // world and paid for in tokens.
                    if (!AgentBridgeMod.GameFocused || AgentBridgeMod.CinematicActive)
                    {
                        SetState(AgentState.Paused);
                        SetStatus(AgentBridgeMod.CinematicActive
                            ? "paused (cinematic)"
                            : "paused (game unfocused)");

                        if (ct.WaitHandle.WaitOne(PauseSliceMs)) break;
                        continue;
                    }

                    if (State == AgentState.Paused)
                    {
                        SetState(AgentState.Running);
                        SetStatus("running");
                    }

                    // ---- pick up events ------------------------------------------------------
                    var events = EventLog.WaitForEvents(_eventCursor, PollSliceMs);
                    if (ct.IsCancellationRequested) break;

                    if (events.Count > 0)
                    {
                        _eventCursor = events[^1].Seq;
                        idleSlices = 0;

                        // Any real event ends every form of back-off.
                        _idleRechecks = 0;

                        CollectBurst(events, ct);
                        if (ct.IsCancellationRequested) break;

                        events = Deduplicate(events);
                    }
                    else
                    {
                        // Idle back-off: 60 s → 120 s → 240 s → 480 s. A narrative or mopping-up
                        // mission would otherwise burn a full round every minute for nothing.
                        var threshold = RecheckAfterSlices * Math.Min(8, 1 << Math.Min(3, _idleRechecks));
                        if (++idleSlices < threshold) continue;

                        idleSlices = 0;
                        _idleRechecks++;
                        events = new List<BridgeEvent> { RecheckEvent() };
                    }

                    Decide(events, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // One LLM timeout or one failed reflection must never end the commander's
                    // shift. Back off, report, carry on.
                    SetStatus($"error: {ex.Message}");
                    AppendLog($"error: {ex.Message}");

                    if (ct.WaitHandle.WaitOne(ErrorBackoffMs)) break;
                    SetStatus("running");
                }
            }
        }
        finally
        {
            // Unconditional, unlike everything else in the loop: this is the one write that must
            // win over a pending Stopping.
            _isStreaming = false;
            _state = (int)AgentState.Stopped;
            _status = "stopped";
        }
    }

    /// <summary>
    /// Keeps pulling until a slice comes back empty or the window closes. A burst — a dispatch
    /// printing line by line, several units revealed at once — must be seen as one picture, not
    /// decided on one fragment at a time.
    /// </summary>
    private void CollectBurst(List<BridgeEvent> events, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + DebounceWindowMs;

        while (Environment.TickCount64 < deadline)
        {
            if (ct.IsCancellationRequested) return;

            var more = EventLog.WaitForEvents(_eventCursor, DebounceSliceMs);
            if (more.Count == 0) return;

            _eventCursor = more[^1].Seq;
            events.AddRange(more);
        }
    }

    /// <summary>
    /// Drops repeats within one batch, keeping the first occurrence and the original order. The
    /// key carries the mission clock as well as the text, so two genuinely separate announcements
    /// that happen to read alike (a countdown ticking past the same wording) both survive.
    /// </summary>
    private static List<BridgeEvent> Deduplicate(List<BridgeEvent> events)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<BridgeEvent>(events.Count);

        foreach (var ev in events)
        {
            if (seen.Add($"{ev.Type}\0{ev.Text}\0{ev.GameTime}")) unique.Add(ev);
        }

        return unique;
    }

    /// <summary>
    /// The synthetic wake-up used by the idle back-off. It asks for a reassessment rather than
    /// for an action: standing by is a legitimate answer and the wording must not push against it.
    /// </summary>
    private static BridgeEvent RecheckEvent() => new()
    {
        Source = "agent",
        Type = "recheck",
        Text = "定时复查: 无新事件, 重新评估当前战场态势",
        GameTime = EventLog.GameClock,
    };

    // =======================================================================================
    // One decision
    // =======================================================================================

    private void Decide(List<BridgeEvent> events, CancellationToken ct)
    {
        var snapshot = MainThread.Run(() => _mod.BuildSnapshot(), WriteTimeoutMs).GetAwaiter().GetResult();
        _snapshot = snapshot;

        _turretKm = MapFrame.LocalToKm(snapshot.TurretMapX, snapshot.TurretMapY);

        var context = new StringBuilder();

        if (_carrySummary.Length > 0)
        {
            context.Append("## 前情简报(此前对话已压缩)\n").Append(_carrySummary).Append("\n\n");
        }

        context.Append("## 新事件(带游戏内任务计时)\n").Append(string.Join("\n", RenderEvents(events)));
        context.Append("\n\n");
        context.Append(BuildCompactState(snapshot));

        // Injected once and only once; a briefing that stayed would be re-read every round.
        _carrySummary = "";

        if (UsageMeter.LastPromptTokens > CompactAtPromptTokens && _messages.Count > CompactMinMessages)
        {
            CompactConversation(ct);
        }

        _messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = context.ToString(),
        });

        SetStatus("thinking...");
        _isStreaming = true;
        _streamingText = "";
        _firesThisRound = 0;

        string reply;
        var buffer = new StringBuilder();
        try
        {
            reply = LlmClient.ChatStream(
                _messages,
                Doctrine.ToolsJson,
                ExecuteTool,
                chunk =>
                {
                    buffer.Append(chunk);
                    // Rebuilt whole and assigned: the panel must never see a StringBuilder mid-edit.
                    _streamingText = buffer.ToString();
                },
                ct);
        }
        finally
        {
            _isStreaming = false;
        }

        SetStatus("running");

        // The final plain text IS the decision rationale — the doctrine asks for one to three
        // sentences and forbids JSON. Nothing is parsed out of it; the shooting already happened
        // in this round's tool calls.
        var reason = reply.Trim();
        if (reason.Length > MaxReasonChars) reason = reason[..MaxReasonChars] + "…";
        _lastReason = reason;

        AppendLog($"决策: {reason}", "decision", new
        {
            events = events.Select(e => $"{e.Source}/{e.Type}").ToList(),
            fires = _firesThisRound,
        });
    }

    /// <summary>
    /// Renders the batch, folding any overflow past <see cref="MaxEventsPerRound"/> into a single
    /// line. The newest events are the ones kept: an hour-old reveal is history, the last minute
    /// is the situation.
    /// </summary>
    private static List<string> RenderEvents(List<BridgeEvent> events)
    {
        var lines = new List<string>();

        var overflow = events.Count - MaxEventsPerRound;
        var start = 0;

        if (overflow > 0)
        {
            start = overflow;
            var earliest = events[0].GameTime;
            lines.Add($"……另有 {overflow} 条更早事件(已省略, 最早 @{earliest})");
        }

        for (var i = start; i < events.Count; i++) lines.Add(FormatEvent(events[i]));
        return lines;
    }

    /// <summary>Single event-line format, shared by the round context and the tool ride-along.</summary>
    private static string FormatEvent(BridgeEvent ev)
    {
        var stamp = ev.GameTime.Length > 0 ? ev.GameTime + " " : "";
        return $"[{stamp}{ev.Source}/{ev.Type}] {ev.Text}";
    }

    private static Dictionary<string, object?> SystemMessage() => new()
    {
        ["role"] = "system",
        ["content"] = Doctrine.SystemPrompt,
    };

    // =======================================================================================
    // Snapshot text
    // =======================================================================================

    /// <summary>
    /// Renders the snapshot into the text the model actually reads. Every line here is protocol:
    /// the system prompt is written against this exact wording, and rephrasing a heading silently
    /// breaks the doctrine that refers to it.
    ///
    /// The turret's own coordinates are never printed. The agent's belief about where its gun
    /// stands may come only from High Command's dispatches and its own registration fire; the one
    /// time the system handed it a coordinate, it copied it back forever.
    /// </summary>
    private static string BuildCompactState(StateSnapshotDto s)
    {
        var sb = new StringBuilder();

        sb.AppendLine(s.GameTime.Length > 0 ? $"## 战场状态 @ {s.GameTime} (任务时钟)" : "## 战场状态");

        AppendMissionType(sb, s);
        AppendMissionIntel(sb, s);

        if (!string.IsNullOrEmpty(s.MapExtentKm))
        {
            sb.AppendLine($"本关地图实测范围: {s.MapExtentKm} — 瞄准点出界会被fire拒绝; 规划盲射/侦察航线前先对照此范围");
        }

        sb.AppendLine(s.TurretCalibrated
            ? "炮塔棋子: 已校准(如需查询假定位置用get_assumed_turret_position)"
            : "炮塔棋子: ⚠本局尚未校准! 出生默认位置不可信, 校准前实体方位/距离均不可信。" +
              "合法校准依据=统帅部电文中的铁巢网格, 或战场/侦查报告中可解算出炮位的观测数据(用solve_target反定位); " +
              "**两者都没有就保持未校准并等待, 绝不猜测/编造坐标**");

        AppendFcs(sb, s.Fcs);

        if (s.InFlightShells.Count > 0)
        {
            sb.AppendLine("在途炮弹(已出膛未落地, 目标已被服务, **严禁重复排队**): " + string.Join(" | ", s.InFlightShells));
        }

        foreach (var gun in s.Guns)
        {
            // IsReloading and CurrentElevation are deliberately withheld: the doctrine forbids
            // planning the queue around load state, and showing it invites exactly that.
            sb.AppendLine($"火炮{gun.Side}: 膛={gun.ChamberedShell ?? "空"} 药={gun.PowderCharges} canFire={gun.CanFire}");
        }

        AppendRequisition(sb, s);
        AppendShellSpecs(sb, s);
        AppendEntities(sb, s);
        AppendMarkers(sb, s);

        return sb.ToString();
    }

    private static void AppendMissionType(StringBuilder sb, StateSnapshotDto s)
    {
        var type = s.MissionType;
        if (string.IsNullOrEmpty(type)) return;

        string meaning;
        if (type == "Chill")
        {
            meaning = "无尽模式(Chill)——敌军无限补充; 摧毁敌炮只延长反炮击倒计时, 不能根治";
        }
        else if (type.StartsWith("Challange", StringComparison.Ordinal)
                 || type.StartsWith("Challenge", StringComparison.Ordinal))
        {
            // "Challange" is the game's own misspelling. Both spellings are accepted so a future
            // fix upstream does not silently demote this mode to "unknown".
            meaning = "无尽模式(Challenging)——敌军无限补充; 摧毁敌炮只延长反炮击倒计时, 不能根治";
        }
        else if (type == "Campaign")
        {
            meaning = "剧本任务——敌军编制有限; 敌炮全灭=反炮击倒计时彻底停止";
        }
        else if (type == "Tutorial")
        {
            meaning = "教程关";
        }
        else
        {
            meaning = $"未知类型 '{type}' (按剧本任务处置)";
        }

        sb.AppendLine("作战模式: " + meaning);
    }

    /// <summary>
    /// Mission name plus every intel entry whose key is a substring of it. All matches are
    /// appended, not just the first: the table is a commander's notebook and two notes about the
    /// same mission are both true.
    /// </summary>
    private static void AppendMissionIntel(StringBuilder sb, StateSnapshotDto s)
    {
        if (string.IsNullOrEmpty(s.MissionName)) return;

        sb.AppendLine($"当前关卡: {s.MissionName}");

        foreach (var (key, intel) in Doctrine.MapIntelTable)
        {
            if (s.MissionName!.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"关卡情报(指挥官提供, 优先于通用学说): {intel}");
            }
        }
    }

    private static void AppendFcs(StringBuilder sb, FcsStatusDto fcs)
    {
        sb.AppendLine(
            $"FCS: pending={fcs.PendingCount} done={fcs.CompletedTaskCount} fail={fcs.FailedTaskCount} " +
            $"| T9(左炮): {fcs.LeftTask ?? "-"} | T10(右炮): {fcs.RightTask ?? "-"}");

        if (fcs.PendingTasks.Count == 0) return;

        sb.AppendLine("FCS待执行(#N=任务唯一编号, adjust/cancel用它; 排列=计划炮击顺序: 优先级带内按方位就近连打):");
        foreach (var task in fcs.PendingTasks) sb.AppendLine("  " + task);
    }

    private static void AppendRequisition(StringBuilder sb, StateSnapshotDto s)
    {
        var shells = new List<string>();
        var special = new List<string>();

        foreach (var card in s.Cards)
        {
            var uses = card.RemainingUses > 0 ? $", 余{card.RemainingUses}次" : "";
            var label = $"{card.Id}({card.Cost}点{uses})";
            (ShellIds.Contains(card.Id) ? shells : special).Add(label);
        }

        if (s.RequisitionPoints.HasValue)
        {
            sb.AppendLine($"征用点余额: {s.RequisitionPoints.Value}点(每次购买实时扣减, 买不起的方案不要排)");
        }

        // Emitted unconditionally: "(未就绪)" is itself information — it says the console has not
        // been read yet, which is not the same as "this mission stocks nothing".
        sb.AppendLine("征用台可购弹种及单价(开火只能从此选, 清单外弹种购买必败): "
                      + (shells.Count > 0 ? string.Join(", ", shells) : "(未就绪)"));

        if (special.Count > 0)
        {
            sb.AppendLine("征用台特殊卡及单价(仅经requisition_card工具使用, 不是弹种, 注意贵价卡值不值得花): "
                          + string.Join(", ", special));
        }
    }

    private static void AppendShellSpecs(StringBuilder sb, StateSnapshotDto s)
    {
        if (s.ShellSpecs.Count == 0) return;

        sb.AppendLine("弹药规格(爆炸半径决定覆盖/友军安全距离; 射程按装药档):");

        foreach (var spec in s.ShellSpecs)
        {
            var salvo = spec.ProjectilesPerShell > 1 ? $"×{spec.ProjectilesPerShell}弹" : "";

            var ranges = spec.ChargeRanges.Count > 0
                ? string.Join(" ", spec.ChargeRanges
                    .OrderBy(r => r.Charge)
                    .Select(r => $"C{r.Charge}:{r.MinKm:F1}-{r.MaxKm:F1}km"))
                : "射程表未知";

            // ImpactRadius is kilometres. Treating it as metres once made every radius render as
            // "0m" and left the friendly-fire gate inert.
            sb.AppendLine($"  {spec.Id}: 爆半径{spec.ImpactRadius * 1000f:F0}m 伤害{spec.Damage}{salvo} {ranges}");
        }
    }

    private static void AppendEntities(StringBuilder sb, StateSnapshotDto s)
    {
        sb.AppendLine("可见实体(entityId必须逐字取自此表):");

        if (s.Entities.Count == 0)
        {
            sb.AppendLine("  (无 — 没有任何目标被揭示)");
            return;
        }

        foreach (var e in s.Entities)
        {
            var immune = e.ImmuneShells.Length > 0 ? " | 免疫:" + string.Join(",", e.ImmuneShells) : "";
            sb.AppendLine(
                $"  {e.Id} | {e.Role} | 甲{e.Armour} | {e.Health}/{e.MaxHealth} | {(e.IsAlive ? "alive" : "DEAD")} " +
                $"| {e.BearingDeg:F1}° | {e.DistanceKm:F2}km{immune}");
        }
    }

    /// <summary>
    /// The player's own artillery tokens. The doctrine promises the model that these are hints
    /// placed by hand, so they have to actually appear. Grid only: a token is a suggestion, and
    /// pretending it carries firing data would invite shooting at it without solving first.
    /// </summary>
    private static void AppendMarkers(StringBuilder sb, StateSnapshotDto s)
    {
        if (s.Markers.Count == 0) return;

        var tokens = s.Markers.Select(m => $"T{m.Id} {GridMath.GridOf(MapFrame.LocalToKm(m.MapX, m.MapY))}");
        sb.AppendLine("玩家标记(玩家手工放置的兴趣点/目标提示, 非系统情报): " + string.Join(", ", tokens));
    }

    // =======================================================================================
    // Tool execution
    // =======================================================================================

    /// <summary>
    /// Uniform post-processing around every tool call. The order is fixed: execute, stamp,
    /// record, then ride events along. The recorded copy is the UNSTAMPED, un-ridden result —
    /// the ride-along is context for the model, not part of what the tool returned.
    /// </summary>
    private string ExecuteTool(string name, JsonElement args)
    {
        var result = Dispatch(name, args);

        // A receipt is true at the moment of EXECUTION, not at the moment the model reads it.
        var clock = EventLog.GameClock;
        if (clock.Length > 0) result = $"[@{clock}] {result}";

        var rawArgs = args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText();
        var shownArgs = rawArgs.Length > MaxToolArgsChars ? rawArgs[..MaxToolArgsChars] + "…" : rawArgs;
        var entry = $"{name}({shownArgs}) → {result}";

        lock (_gate)
        {
            _recentToolCalls.Add(entry);
            if (_recentToolCalls.Count > MaxRecentToolCalls)
            {
                _recentToolCalls.RemoveRange(0, _recentToolCalls.Count - MaxRecentToolCalls);
            }
        }

        TransactionLog.Write("tool", entry, new { name, args = rawArgs, result });

        // Events that arrived while the tool ran ride back on its receipt, and the cursor moves
        // with them so the main loop will not re-deliver them. This is what lets the agent react
        // to a friendly-fire warning or an impact inside the same round instead of the next one.
        // Its own actions echo back here too, which is a harmless confirmation.
        var carried = EventLog.WaitForEvents(_eventCursor, 0);
        if (carried.Count > 0)
        {
            _eventCursor = carried[^1].Seq;
            result += "\n[随查战场新事件]\n" + string.Join("\n", carried.Select(FormatEvent));
        }

        return result;
    }

    private string Dispatch(string name, JsonElement args) => name switch
    {
        "grid_to_km" => GridMath.GridToKm(args, _turretKm),

        // Old tool names the model still reaches for. Kept deliberately: a hallucinated name
        // costs a wasted round, and these two aliases have been observed in the wild.
        "set_assumed_turret_position" or "set_turret_position" => SetTurretPosition(args),
        "get_assumed_turret_position" or "get_turret_position" => GetTurretPosition(),

        "fire" => ExecuteFire(args),
        "adjust_fire" => AdjustFire(args),
        "cancel_pending_task" => CancelTask(args),
        "requisition_card" => RequisitionCard(args),
        "signal_horn" => SignalHorn(),

        "firing_solution" => FiringSolution(args),
        "distance_between" => DistanceBetween(args),
        "entities_near" => EntitiesNear(args),
        "solve_target" => SolveTarget(args),

        // The one tool that answers in bare text rather than JSON. Intentional: a calculator
        // result is read, not parsed, and the wrapper would be pure token overhead.
        "calc" => Calculator.Evaluate(args),

        _ => UnknownTool(name, args),
    };

    /// <summary>
    /// Last-resort tolerance for a hallucinated tool name. An unknown call carrying an
    /// <c>actions</c> array is the batch shape the model sometimes invents; each element is run
    /// as a plain fire so the intent is not simply dropped.
    /// </summary>
    private string UnknownTool(string name, JsonElement args)
    {
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("actions", out var actions)
            && actions.ValueKind == JsonValueKind.Array)
        {
            // Inner receipts nest as STRINGS, not as objects: each one is already a complete
            // tool result and re-parsing it would lose its exact wording.
            var results = actions.EnumerateArray().Select(ExecuteFire).ToList();
            return JsonSerializer.Serialize(new { results }, Json);
        }

        return Error($"unknown tool '{name}'");
    }

    // ---------------------------------------------------------------- action tools

    private string ExecuteFire(JsonElement args)
    {
        var req = new FireMissionRequest
        {
            EntityId = Str(args, "entityId"),
            TargetPoint = Str(args, "target"),
            BearingDeg = Num(args, "bearingDeg"),
            DistanceKm = Num(args, "distanceKm"),
            Shell = Str(args, "shell") ?? "HE",
            Priority = Int(args, "priority") is { } p ? Math.Clamp(p, 0, 100) : 50,
            ValidForSeconds = Num(args, "validForSeconds"),
            OffsetKmX = Num(args, "offsetKmX"),
            OffsetKmY = Num(args, "offsetKmY"),
            AllowDangerouslyFriendlyFire = IsTrue(args, "allowDangerouslyFriendlyFire"),
            MotionFrom = Str(args, "motionFrom"),
            MotionBearingDeg = Num(args, "motionBearingDeg"),
            MotionSpeedKmh = Num(args, "motionSpeedKmh"),
            MotionAtTime = Str(args, "motionAtTime"),
        };

        var label = req.EntityId
                    ?? req.TargetPoint
                    ?? $"{req.BearingDeg ?? 0f:F1}°/{req.DistanceKm ?? 0f:F2}km";

        var result = MainThread.Run(() => _mod.QueueFireMission(req), WriteTimeoutMs).GetAwaiter().GetResult();

        // Counted only when FCS accepted. A refused mission is not an action taken, and treating
        // it as one would hide a round in which the agent achieved nothing at all.
        if (result.StartsWith("ok", StringComparison.Ordinal)) _firesThisRound++;

        AppendLog($"fire {label} ({req.Shell}, P{req.Priority}) -> {result}", "fire", new { req, result });
        return Wrap(result);
    }

    private string AdjustFire(JsonElement args)
    {
        var serial = ReadSerial(args);
        if (serial == null) return Error("serial required (任务唯一编号#N)");

        var req = new AdjustFireRequest
        {
            Serial = serial.Value,
            EntityId = Str(args, "entityId"),
            TargetPoint = Str(args, "target"),
            OffsetKmX = Num(args, "offsetKmX"),
            OffsetKmY = Num(args, "offsetKmY"),
            AllowDangerouslyFriendlyFire = IsTrue(args, "allowDangerouslyFriendlyFire"),
        };

        var result = MainThread.Run(() => _mod.AdjustFireMission(req), WriteTimeoutMs).GetAwaiter().GetResult();

        AppendLog($"adjust #{req.Serial} -> {result}", "adjust", new { req, result });
        return Wrap(result);
    }

    private string CancelTask(JsonElement args)
    {
        var serial = ReadSerial(args);
        if (serial == null) return Error("serial required (任务唯一编号#N)");

        var value = serial.Value;
        var result = MainThread.Run(() => _mod.CancelPendingFcsTask(value), WriteTimeoutMs).GetAwaiter().GetResult();

        AppendLog($"cancel #{value} -> {result}", "cancel", new { serial = value, result });
        return Wrap(result);
    }

    private string RequisitionCard(JsonElement args)
    {
        var cardId = Str(args, "cardId");
        if (string.IsNullOrWhiteSpace(cardId)) return Error("cardId required");

        var bearing = Num(args, "bearingDeg");
        var distance = Num(args, "distanceKm");
        var startGrid = Str(args, "startGrid");
        var priority = Int(args, "priority") is { } p ? Math.Clamp(p, 0, 100) : 50;

        // No AppendLog here on purpose: the mod writes the "requisition" transaction when the
        // purchase actually completes, and logging the request too would double-count it.
        var result = MainThread
            .Run(() => _mod.RequestCard(cardId!, bearing, priority, startGrid, distance), WriteTimeoutMs)
            .GetAwaiter().GetResult();

        return Wrap(result);
    }

    private string SignalHorn()
        => Wrap(MainThread.Run(() => _mod.PullSignalHorn(), ReadTimeoutMs).GetAwaiter().GetResult());

    // ---------------------------------------------------------------- turret tools

    private string SetTurretPosition(JsonElement args)
    {
        var position = Str(args, "position") ?? "";

        var km = GridMath.ParsePoint(position, _turretKm);
        if (km == null) return Error($"cannot parse position '{position}' (grid like 'H2 3:4' or 'kmX,kmY')");

        var x = km.Value.x;
        var y = km.Value.y;

        var outcome = MainThread.Run(() =>
        {
            var message = _mod.SetDeclaredTurret(x, y);
            var local = _mod.ReadTurretLocal();
            return (Message: message, LocalX: local.x, LocalY: local.y);
        }, WriteTimeoutMs).GetAwaiter().GetResult();

        // Re-freeze the round's origin on the piece's ACTUAL position. Reading it back rather
        // than assuming the requested point means a refused placement leaves the origin
        // untouched, with no string-sniffing of the receipt.
        _turretKm = MapFrame.LocalToKm(outcome.LocalX, outcome.LocalY);

        AppendLog($"turret declared at km({x:F2},{y:F2})", "turret", new { x, y });
        return Wrap(outcome.Message);
    }

    /// <summary>
    /// Reads the LIVE piece position, not the round's frozen origin: this tool exists to answer
    /// "where do you currently think you are", and a stale answer to that is worse than none.
    /// </summary>
    private string GetTurretPosition()
    {
        var local = MainThread.Run(() => _mod.ReadTurretLocal(), ReadTimeoutMs).GetAwaiter().GetResult();
        var km = MapFrame.LocalToKm(local.x, local.y);

        if (!GridMath.InMapBounds(km))
        {
            // No coordinates in this branch: an out-of-map value is not a position, and handing
            // it over invites the model to reason from it anyway.
            return JsonSerializer.Serialize(new
            {
                unreliable = true,
                note = "假定炮塔位置在地图之外, 不可信。用其他信息(统帅部电文的铁巢网格/侦查报告反定位)重新set_assumed_turret_position。",
            }, Json);
        }

        return JsonSerializer.Serialize(new
        {
            kmX = Round(km.x, 3),
            kmY = Round(km.y, 3),
            grid = GridMath.GridOf(km),
        }, Json);
    }

    // ---------------------------------------------------------------- geometry tools

    /// <summary>Also reads the live turret position — it is gunnery data, not a map lookup.</summary>
    private string FiringSolution(JsonElement args)
    {
        var local = MainThread.Run(() => _mod.ReadTurretLocal(), ReadTimeoutMs).GetAwaiter().GetResult();
        var turret = MapFrame.LocalToKm(local.x, local.y);

        (float x, float y) point;
        string label;

        var entityId = Str(args, "entityId");
        var target = Str(args, "target");

        if (!string.IsNullOrWhiteSpace(entityId))
        {
            var id = entityId!;
            var entity = MainThread.Run(() => _mod.FindVisibleEntity(id), ReadTimeoutMs).GetAwaiter().GetResult();
            if (entity == null) return Error($"entity '{id}' not visible on the map");

            point = MapFrame.LocalToKm(entity.MapX, entity.MapY);
            label = id;
        }
        else if (!string.IsNullOrWhiteSpace(target))
        {
            var parsed = GridMath.ParsePoint(target, turret);
            if (parsed == null) return Error($"cannot parse target '{target}'");

            point = parsed.Value;
            label = target!;
        }
        else
        {
            return Error("need target or entityId");
        }

        return JsonSerializer.Serialize(new
        {
            target = label,
            bearingDeg = Round(BearingBetween(turret, point), 2),
            distanceKm = Round(DistanceBetweenKm(turret, point), 3),
            turretKm = new { x = Round(turret.x, 3), y = Round(turret.y, 3) },
            inMapBounds = GridMath.InMapBounds(point),
        }, Json);
    }

    private string DistanceBetween(JsonElement args)
    {
        var specA = Str(args, "a");
        var specB = Str(args, "b");

        var a = ResolvePoint(specA);
        if (a == null) return Error($"cannot resolve a='{specA}' (not a visible entityId, 'turret', grid, or 'kmX,kmY')");

        var b = ResolvePoint(specB);
        if (b == null) return Error($"cannot resolve b='{specB}' (not a visible entityId, 'turret', grid, or 'kmX,kmY')");

        return JsonSerializer.Serialize(new
        {
            a = new { label = a.Value.Label, kmX = Round(a.Value.Km.x, 3), kmY = Round(a.Value.Km.y, 3) },
            b = new { label = b.Value.Label, kmX = Round(b.Value.Km.x, 3), kmY = Round(b.Value.Km.y, 3) },
            distanceKm = Round(DistanceBetweenKm(a.Value.Km, b.Value.Km), 3),
            bearingDegAtoB = Round(BearingBetween(a.Value.Km, b.Value.Km), 1),
        }, Json);
    }

    /// <summary>
    /// Neighbourhood scan over the round's snapshot. Neither dead entities nor friendly ones are
    /// filtered out: this is the tool the model uses to vet a blast area before asking for it,
    /// and a filtered answer would hide exactly what it is checking for.
    /// </summary>
    private string EntitiesNear(JsonElement args)
    {
        var spec = Str(args, "center");
        var center = ResolvePoint(spec);
        if (center == null)
        {
            return Error($"cannot resolve center='{spec}' (not a visible entityId, 'turret', grid, or 'kmX,kmY')");
        }

        var radiusKm = Num(args, "radiusKm") is { } r ? Math.Clamp(r, 0.05f, 30.0f) : 1.0f;

        var hits = new List<object>();
        var snapshot = _snapshot;

        if (snapshot != null)
        {
            var found = snapshot.Entities
                .Select(e => (Entity: e, Km: MapFrame.LocalToKm(e.MapX, e.MapY)))
                .Select(t => (t.Entity, Distance: DistanceBetweenKm(center.Value.Km, t.Km), t.Km))
                .Where(t => t.Distance <= radiusKm && t.Entity.Id != center.Value.Label)
                .OrderBy(t => t.Distance)
                .Take(30);

            foreach (var (entity, distance, km) in found)
            {
                hits.Add(new
                {
                    id = entity.Id,
                    role = entity.Role,
                    isAlive = entity.IsAlive,
                    distanceKm = Round(distance, 3),
                    bearingDeg = Round(BearingBetween(center.Value.Km, km), 1),
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            center = new
            {
                label = center.Value.Label,
                kmX = Round(center.Value.Km.x, 3),
                kmY = Round(center.Value.Km.y, 3),
            },
            radiusKm,
            count = hits.Count,
            entities = hits,
        }, Json);
    }

    private string SolveTarget(JsonElement args)
    {
        var result = GridMath.SolveTarget(args, _turretKm, out var geometry);

        // Purely decorative, so it is posted rather than run: the agent must never wait on the
        // frame loop to draw a line.
        if (geometry.Solution != null) MainThread.Post(() => PlotGeometry(geometry));

        return result;
    }

    /// <summary>
    /// Draws the solve on the physical command table: observation strokes in yellow, range
    /// circles with the compass prefab (origin = centre, target = a point on the rim), and the
    /// solution as a zero-length red stroke, which is how this game spells "a dot".
    /// </summary>
    private static void PlotGeometry(GridMath.SolveGeometry geometry)
    {
        foreach (var (start, end) in geometry.Lines)
        {
            MapDrawer.Draw(0, MapDrawer.PrefabYellow, new Vector2(start.x, start.y), new Vector2(end.x, end.y));
        }

        foreach (var (center, radiusKm) in geometry.Circles)
        {
            MapDrawer.Draw(0, MapDrawer.PrefabCompass,
                new Vector2(center.x, center.y),
                new Vector2(center.x + radiusKm, center.y));
        }

        if (geometry.Solution is { } solution)
        {
            MapDrawer.Draw(0, MapDrawer.PrefabRed,
                new Vector2(solution.x, solution.y),
                new Vector2(solution.x, solution.y));
        }
    }

    /// <summary>
    /// Resolves an endpoint against the round's snapshot alone — no main-thread trip. Order is
    /// fixed: the literal "turret", then a snapshot entity by exact id, then a grid or km pair.
    /// Entity ids are matched case-sensitively because the snapshot hands them out to be echoed
    /// back verbatim.
    /// </summary>
    private ((float x, float y) Km, string Label)? ResolvePoint(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;

        if (string.Equals(spec!.Trim(), "turret", StringComparison.OrdinalIgnoreCase))
        {
            return (_turretKm, "turret");
        }

        var snapshot = _snapshot;
        if (snapshot != null)
        {
            foreach (var entity in snapshot.Entities)
            {
                if (entity.Id != spec && entity.RawId != spec) continue;
                return (MapFrame.LocalToKm(entity.MapX, entity.MapY), entity.Id);
            }
        }

        var km = GridMath.ParsePoint(spec, _turretKm);
        return km == null ? null : (km.Value, spec!);
    }

    // =======================================================================================
    // Compaction
    // =======================================================================================

    /// <summary>
    /// Trades the whole conversation for a handover briefing. It costs one extra API call and
    /// guarantees a cache miss on the next round, which is why it only fires once the context has
    /// grown past the point where carrying it is the more expensive option.
    /// </summary>
    private void CompactConversation(CancellationToken ct)
    {
        AppendLog(
            "auto-compact: context "
            + UsageMeter.LastPromptTokens.ToString("N0", CultureInfo.InvariantCulture)
            + " tokens > "
            + CompactAtPromptTokens.ToString("N0", CultureInfo.InvariantCulture),
            "compact");

        SetStatus("compacting...");

        _messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = CompactPrompt,
        });

        // No tools and no streaming echo: this is bookkeeping, not a decision.
        var summary = LlmClient.ChatStream(_messages, null, null, _ => { }, ct);

        _messages.Clear();
        _messages.Add(SystemMessage());

        // Held outside the history and injected into the next user message exactly once. Putting
        // it into the history instead would make it part of the prefix forever.
        _carrySummary = summary;

        TransactionLog.Write("compact", "conversation compacted", new { summary });
    }

    // =======================================================================================
    // Logging and small helpers
    // =======================================================================================

    private void AppendLog(string text, string type = "agent", object? data = null)
    {
        lock (_gate)
        {
            _log.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
            if (_log.Count > MaxLogEntries) _log.RemoveRange(0, _log.Count - MaxLogEntries);
        }

        // Outside the lock: the transaction log writes to disk.
        TransactionLog.Write(type, text, data);
    }

    /// <summary>
    /// Status writes are suppressed while a stop is pending, so "stopping (finishing current
    /// round)" survives until the thread really is gone instead of being overwritten by the
    /// round it is busy finishing.
    /// </summary>
    private void SetStatus(string status)
    {
        if (State != AgentState.Stopping) _status = status;
    }

    private void SetState(AgentState state)
    {
        if (State != AgentState.Stopping) _state = (int)state;
    }

    /// <summary>Bearing from a to b: 0 = map north, increasing clockwise, degrees.</summary>
    private static float BearingBetween((float x, float y) from, (float x, float y) to)
    {
        var degrees = MathF.Atan2(to.x - from.x, to.y - from.y) * 180f / MathF.PI;
        return degrees < 0f ? degrees + 360f : degrees;
    }

    private static float DistanceBetweenKm((float x, float y) a, (float x, float y) b)
    {
        var dx = b.x - a.x;
        var dy = b.y - a.y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    private static double Round(float value, int digits) => Math.Round((double)value, digits);

    private static string Wrap(string result) => JsonSerializer.Serialize(new { result }, Json);

    private static string Error(string message) => JsonSerializer.Serialize(new { error = message }, Json);

    /// <summary>Serial with its hallucination-tolerance alias; both are read as numbers.</summary>
    private static int? ReadSerial(JsonElement args) => Int(args, "serial") ?? Int(args, "targetId");

    private static string? Str(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static float? Num(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number)
            ? (float)number
            : null;

    private static int? Int(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetDouble(out var number)
            ? (int)number
            : null;

    /// <summary>
    /// Strictly the JSON boolean <c>true</c>. The string "true" and the number 1 do not count:
    /// this flag waives friendly-fire protection and must never be granted by a type coercion.
    /// </summary>
    private static bool IsTrue(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object
           && args.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.True;
}
