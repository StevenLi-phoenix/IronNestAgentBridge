using System.Text.Json;
using MelonLoader;

namespace IronNestAgentBridge.Agent;

/// <summary>
/// The fire-direction-officer agent loop, fully in-process (no external runtime).
/// Runs on a background thread; every game access is marshalled through MainThread.
/// Port of agent/agent.py with the same doctrine prompt and queue discipline.
/// </summary>
public class FdoAgent
{
    private const string SystemPrompt = """
你是重型要塞炮"铁巢"的射击指挥官(FDC)。你会收到:
- 最高统帅部电文(primary): 任务指令、弹药限制、反炮兵警告
- 战场报告(secondary): 观测员的方位角交汇报告
- 指挥桌事件(map): 新揭示/移动/受损/摧毁的目标
- state快照: 所有可见目标的方位角/距离/护甲/免疫弹种、火炮与FCS状态

你的职责是战术决策: 打谁、用什么弹、什么顺序。执行完全由FCS自动完成:
你排任务后FCS会自动购弹、装填、装药、调仰角、转炮塔。**任何时候都可以排任务**,
不要因为guns显示isReloading/canFire=false而等待——那是炮的常驻机械状态,
FCS会处理好一切。fcs.pendingCount/leftTask/rightTask才反映任务执行进度。
规则:
- 遵守统帅部电文中的弹药限制与优先目标指令
- 弹种选择: armour=0的目标(步兵/无甲车辆)用HE即可; armour>=1的目标HE大概率"未击穿",
  直接用APHE(兼具穿甲和爆破)或AP。role含Fortification或rawId为supplycash/
  hostilebunker等工事类=地下/加固目标, 必须AP系穿甲弹。immuneShells非空时严禁选名单内弹种
- 反炮兵威胁下优先高价值目标
- 战争迷雾: entities[]是当前唯一的已揭示目标清单, 为空就说明没有任何目标被揭示。
  entityId必须一字不差地取自entities[]里实际存在的id, 严禁凭空猜测或编造id。
  未揭示目标只能根据电报情报三角定位后用bearingDeg+distanceKm盲射
  (方位角以炮塔为原点, 正北=0°顺时针; 距离单位km)。
- 定位计算(必须用工具, 严禁手算三角函数——手算漂移是脱靶主因):
  * grid_to_km: 电文网格(如"G6 5:3")转km坐标并给出炮塔到该点的射击诸元
  * solve_target: 观测线/距离圆交汇解算。战场报告的"自X的方位角B°"是一条line
    {from:"X的网格", bearingDeg:B}; "自X距离D"是一个circle {from:..., distanceKm:D};
    "自X方位角B及距离D"是line带distanceKm(直接定位)。把报告数据原样填进工具,
    返回值里的bearingDeg/distanceKm直接用于开火action。
  你只负责从电文中抄录观测数据和选择组合, 数值计算一律交给工具。
- 盲射精度认知: 情报本身有量化误差(网格±0.05km、方位角±0.5°), 远距离斜交线解算
  误差被放大。盲射=效力侦察(ranging fire): 第一发的价值是炸开迷雾揭示目标。
  弹着揭示目标(entity_revealed事件)后, 立即用entityId对其精确补射, 那才是摧毁手段。
  同一目标若有"方位角+距离"组合优先用它, 且优先选距目标近的观测员的数据。
- 弹药成本(征用点): STAR=2, HE/AP=18。因此侦察性盲射一律用STAR——它的任务是照亮/
  揭示区域, 不是摧毁; 用AP/HE盲射等于花9倍的钱赌一发不准的弹。只有对已揭示目标
  (entityId)才花HE/AP做摧毁性射击。例外: 统帅部明确限制弹种时从其指令。
- 每次决策输出JSON, 两种action格式, 每个action可带priority(0-100, 默认50):
  {"actions": [{"entityId": "<必须是entities[]中存在的id>", "shell": "HE", "priority": 50},
               {"bearingDeg": 75.0, "distanceKm": 9.1, "shell": "AP", "priority": 30}],
   "reason": "..."}
  不开火时输出 {"actions": [], "reason": "..."}
- priority规则: 反炮兵/敌方炮兵威胁=90以上(FCS会跳过凑单等待立即抢占下一门空炮);
  统帅部点名的优先目标=70; 常规高价值(仓库/工事/指挥所)=60; 普通目标=50;
  低价值步兵/补刀=30。你的任务先进入内部优先队列, 由mod按优先级在FCS空闲时下发,
  下发前会自动复核目标存活——所以你可以放心把发现的目标都排上, 优先级排对即可。
- 队列纪律(最重要): fcs.pendingTasks列出所有待执行任务(若无此字段则以pendingCount计数),
  每个任务执行约需1分钟, 队列会自动逐个打完。目标在pendingTasks/你的决策历史里已有
  未执行完的任务时, 严禁再排——"已下达"不等于"已打完", 你看不到弹着不代表任务丢了。
  补射的唯一条件: 收到该目标明确的未击穿/未命中报告, 且队列中已无针对它的任务。
  已摧毁(isAlive=false)的目标绝不再排。宁可这轮不开火, 也不要堆积队列浪费弹药。
""";

    private const string ToolsJson = """
[
  {
    "type": "function",
    "function": {
      "name": "grid_to_km",
      "description": "把电文网格坐标(如'G6 5:3')转换为km坐标, 并返回炮塔到该点的方位角与距离",
      "parameters": {
        "type": "object",
        "properties": { "grid": { "type": "string", "description": "网格, 如 'G6 5:3'" } },
        "required": ["grid"]
      }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "solve_target",
      "description": "由观测线/距离圆精确解算目标位置, 返回km坐标和炮塔射击诸元(bearingDeg/distanceKm)。所有三角定位必须用本工具。",
      "parameters": {
        "type": "object",
        "properties": {
          "lines": {
            "type": "array",
            "items": {
              "type": "object",
              "properties": {
                "from": { "type": "string", "description": "观测点: 网格'G6 5:3'、'turret'或'kmX,kmY'" },
                "bearingDeg": { "type": "number" },
                "distanceKm": { "type": "number", "description": "可选; 与bearingDeg同给时直接定位" }
              },
              "required": ["from", "bearingDeg"]
            }
          },
          "circles": {
            "type": "array",
            "items": {
              "type": "object",
              "properties": {
                "from": { "type": "string" },
                "distanceKm": { "type": "number" }
              },
              "required": ["from", "distanceKm"]
            }
          },
          "near": { "type": "string", "description": "可选; 解有歧义时取靠近此点的解" }
        }
      }
    }
  }
]
""";

    private const int PollSliceMs = 5_000;
    private const int RecheckAfterSlices = 5; // 5 x 5s = idle re-evaluation cadence
    private const double MapLocalToKm = 3.8164;
    private const double MapOffsetX = 10.016;
    private const double MapOffsetY = 5.235;
    // Auto-compact the conversation once the cached prefix grows past this many prompt tokens.
    private const long CompactAtPromptTokens = 100_000;

    // Persistent conversation: system + every turn (incl. tool rounds) stays byte-identical
    // across decisions so the provider's prefix cache hits on all history.
    private readonly List<object> _messages = new();
    private string _carrySummary = "";

    private readonly AgentBridgeMod _mod;
    private readonly object _gate = new();
    private readonly List<string> _history = new();
    private readonly List<string> _log = new();
    private readonly List<string> _recentToolCalls = new();

    public List<string> RecentToolCalls()
    {
        lock (_gate) return _recentToolCalls.ToList();
    }

    private Thread? _thread;
    private CancellationTokenSource? _cts;

    public FdoAgent(AgentBridgeMod mod) => _mod = mod;

    public bool IsRunning => _thread is { IsAlive: true };
    public string Status { get; private set; } = "stopped";
    public string LastReason { get; private set; } = "";

    /// <summary>Live LLM output while a decision streams in; empty when idle. Read by the UI each frame.</summary>
    public string StreamingText { get; private set; } = "";
    public bool IsStreaming { get; private set; }

    public IReadOnlyList<string> LogSnapshot()
    {
        lock (_gate) return _log.ToList();
    }

    public void Start()
    {
        if (IsRunning) return;
        if (!AgentConfig.LlmControl)
        {
            Status = "LLM control disabled";
            return;
        }
        if (string.IsNullOrWhiteSpace(AgentConfig.ApiKey))
        {
            Status = "no ApiKey — set [AgentBridge] ApiKey in UserData\\MelonPreferences.cfg";
            return;
        }
        _cts = new CancellationTokenSource();
        _messages.Clear();
        _messages.Add(new Dictionary<string, object?> { ["role"] = "system", ["content"] = SystemPrompt });
        _carrySummary = "";
        _thread = new Thread(() => Loop(_cts.Token)) { IsBackground = true, Name = "AgentBridge-FDO" };
        _thread.Start();
        Status = "running";
        AppendLog("agent started");
    }

    public void Stop()
    {
        _cts?.Cancel();
        Status = "stopped";
        AppendLog("agent stopped");
    }

    public void ClearLog()
    {
        lock (_gate) { _log.Clear(); _history.Clear(); }
    }

    private void AppendLog(string text, string type = "agent", object? data = null)
    {
        lock (_gate)
        {
            _log.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
            if (_log.Count > 300)
                _log.RemoveRange(0, _log.Count - 300);
        }
        TransactionLog.Write(type, text, data);
    }

    private void Loop(CancellationToken ct)
    {
        long since = 0;
        var idleSlices = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Mirror FCS's focus gate: no decisions (and no token spend) while the game
                // is in the background; FCS pauses its automation there anyway.
                if (!AgentBridgeMod.GameFocused)
                {
                    Status = "paused (game unfocused)";
                    if (ct.WaitHandle.WaitOne(1_000)) break;
                    continue;
                }
                if (Status == "paused (game unfocused)")
                    Status = "running";

                var events = EventLog.WaitForEvents(since, PollSliceMs);
                if (ct.IsCancellationRequested) break;

                if (events.Count > 0)
                {
                    since = events[^1].Seq;
                    idleSlices = 0;
                }
                else
                {
                    if (++idleSlices < RecheckAfterSlices)
                        continue;
                    idleSlices = 0;
                    events = new List<BridgeEvent>
                    {
                        new() { Source = "agent", Type = "recheck", Text = "定时复查: 无新事件, 重新评估当前战场态势" },
                    };
                }

                Decide(events, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Status = $"error: {ex.Message}";
                AppendLog($"error: {ex.Message}");
                if (ct.WaitHandle.WaitOne(5_000)) break;
                Status = "running";
            }
        }
    }

    private string BuildCompactState(StateSnapshotDto s)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 战场状态");
        sb.AppendLine($"炮塔km: ({MapOffsetX + s.TurretMapX * MapLocalToKm:F2}, {MapOffsetY + s.TurretMapY * MapLocalToKm:F2})");
        sb.AppendLine($"FCS: pending={s.Fcs.PendingCount} done={s.Fcs.CompletedTaskCount} fail={s.Fcs.FailedTaskCount}"
                      + $" | L: {s.Fcs.LeftTask ?? "-"} | R: {s.Fcs.RightTask ?? "-"}");
        if (s.Fcs.PendingTasks.Count > 0)
        {
            sb.AppendLine("FCS待执行:");
            foreach (var t in s.Fcs.PendingTasks)
                sb.AppendLine("  " + t);
        }
        var staged = _mod.MissionQueue.Describe();
        sb.AppendLine("内部优先队列(staged待下发, 勿重复): " + (staged.Count == 0 ? "(空)" : string.Join(" | ", staged)));
        foreach (var g in s.Guns)
            sb.AppendLine($"火炮{g.Side}: 膛={g.ChamberedShell ?? "空"} 药={g.PowderCharges} canFire={g.CanFire}");
        sb.AppendLine("可见实体(entityId必须逐字取自此表):");
        if (s.Entities.Count == 0)
            sb.AppendLine("  (无 — 没有任何目标被揭示)");
        foreach (var e in s.Entities)
        {
            sb.Append($"  {e.Id} | {e.Role} | 甲{e.Armour} | {e.Health}/{e.MaxHealth} | {(e.IsAlive ? "alive" : "DEAD")}"
                      + $" | {e.BearingDeg:F1}° | {e.DistanceKm:F2}km");
            if (e.ImmuneShells.Length > 0)
                sb.Append(" | 免疫:" + string.Join(",", e.ImmuneShells));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Auto-compact: summarize the conversation into a battle brief, then restart the
    /// message history from it. Costs one summary call; the next round is a cache miss.
    /// </summary>
    private void CompactConversation(CancellationToken ct)
    {
        AppendLog($"auto-compact: context {UsageMeter.LastPromptTokens:N0} tokens > {CompactAtPromptTokens:N0}", "compact");
        Status = "compacting...";
        _messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = "请把到目前为止的战况压缩成一份接班简报, 只输出简报文本: " +
                          "1)已确认摧毁的目标 2)已下达但未确认结果的任务 3)存活/待处理目标与其弹种方案 " +
                          "4)观测员/参考点网格等长期情报 5)已学到的弹药与精度教训 6)统帅部的有效指令与限制",
        });
        var summary = LlmClient.ChatStream(_messages, null, null, _ => { }, ct);
        _messages.Clear();
        _messages.Add(new Dictionary<string, object?> { ["role"] = "system", ["content"] = SystemPrompt });
        _carrySummary = summary;
        TransactionLog.Write("compact", "conversation compacted", new { summary });
    }

    private void Decide(List<BridgeEvent> events, CancellationToken ct)
    {
        var snapshot = MainThread.Run(() => _mod.BuildSnapshot(), 15_000).GetAwaiter().GetResult();

        var context =
            (_carrySummary.Length > 0 ? "## 前情简报(此前对话已压缩)\n" + _carrySummary + "\n\n" : "") +
            "## 新事件\n" + string.Join("\n", events.Select(e => $"[{e.Source}/{e.Type}] {e.Text}")) +
            "\n\n" + BuildCompactState(snapshot);
        _carrySummary = "";

        var turretKm = (
            x: MapOffsetX + snapshot.TurretMapX * MapLocalToKm,
            y: MapOffsetY + snapshot.TurretMapY * MapLocalToKm);

        string ExecuteTool(string name, JsonElement args)
        {
            var result = name switch
            {
                "grid_to_km" => GridMath.GridToKm(args, turretKm),
                "solve_target" => GridMath.SolveTarget(args, turretKm),
                _ => JsonSerializer.Serialize(new { error = $"unknown tool '{name}'" }),
            };
            var argsText = args.GetRawText();
            var entry = $"{name}({(argsText.Length > 120 ? argsText[..120] + "…" : argsText)}) → {result}";
            lock (_gate)
            {
                _recentToolCalls.Add(entry);
                if (_recentToolCalls.Count > 20)
                    _recentToolCalls.RemoveRange(0, _recentToolCalls.Count - 20);
            }
            TransactionLog.Write("tool", entry, new { name, args = argsText, result });
            return result;
        }

        if (UsageMeter.LastPromptTokens > CompactAtPromptTokens && _messages.Count > 3)
            CompactConversation(ct);

        _messages.Add(new Dictionary<string, object?> { ["role"] = "user", ["content"] = context });

        Status = "thinking...";
        IsStreaming = true;
        StreamingText = "";
        var buffer = new System.Text.StringBuilder();
        string reply;
        try
        {
            reply = LlmClient.ChatStream(_messages, ToolsJson, ExecuteTool, chunk =>
            {
                buffer.Append(chunk);
                StreamingText = buffer.ToString();
            }, ct);
        }
        finally
        {
            IsStreaming = false;
        }
        Status = "running";

        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            AppendLog("LLM reply had no JSON, skipped");
            return;
        }

        using var doc = JsonDocument.Parse(reply[start..(end + 1)]);
        var reason = doc.RootElement.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
        LastReason = reason;
        AppendLog($"决策: {reason}", "decision",
            new { events = events.Select(e => $"{e.Source}/{e.Type}").ToList(), reply });

        var stamp = DateTime.Now.ToString("HH:mm:ss");
        if (!doc.RootElement.TryGetProperty("actions", out var actions) || actions.GetArrayLength() == 0)
        {
            lock (_gate) _history.Add($"[{stamp}] 不开火: {reason}");
            return;
        }

        foreach (var action in actions.EnumerateArray())
        {
            var req = new FireMissionRequest
            {
                EntityId = action.TryGetProperty("entityId", out var id) ? id.GetString() : null,
                BearingDeg = action.TryGetProperty("bearingDeg", out var b) ? b.GetSingle() : null,
                DistanceKm = action.TryGetProperty("distanceKm", out var d) ? d.GetSingle() : null,
                Shell = action.TryGetProperty("shell", out var s) ? s.GetString() ?? "HE" : "HE",
                Priority = action.TryGetProperty("priority", out var p) ? Math.Clamp(p.GetInt32(), 0, 100) : 50,
            };
            var label = req.EntityId ?? $"{req.BearingDeg:F1}°/{req.DistanceKm:F2}km";

            if (AgentConfig.PriorityQueue)
            {
                _mod.MissionQueue.Add(req, req.Priority, label);
                AppendLog($"staged P{req.Priority} {label} ({req.Shell})", "staged", req);
                lock (_gate) _history.Add($"[{stamp}] staged P{req.Priority} {label} {req.Shell}");
            }
            else
            {
                var result = MainThread.Run(() => _mod.QueueFireMission(req), 15_000).GetAwaiter().GetResult();
                AppendLog($"fire {label} ({req.Shell}, P{req.Priority}) -> {result}", "fire", new { req, result });
                lock (_gate) _history.Add($"[{stamp}] fire {label} {req.Shell} -> {result}");
            }
        }
    }
}
