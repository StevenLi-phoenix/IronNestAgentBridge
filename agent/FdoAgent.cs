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
- 坐标换算: 电文中的网格如"H5 0:9"表示 kmX=字母序号+第一个子格/10 (A=0,B=1,...,H=7,
  即kmX≈7.0), kmY=(行号-1)+第二个子格/10 (即kmY≈4.9)。快照中的mapX/mapY换算:
  kmX=10.016+mapX*3.8164, kmY=5.235+mapY*3.8164。两点间: dx=kmX2-kmX1, dy=kmY2-kmY1,
  距离=sqrt(dx²+dy²) km, 从点1看点2的方位角=atan2(dx,dy)转成0~360°。
  炮塔自身位置见快照turretMapX/turretMapY(注意先换算成km坐标再参与计算)。
  战场报告给出的"自X的方位角"是从X点出发的观测线, 两条线相交即目标位置;
  "自X距离Y"则是以X为圆心的圆。逐步写出你的计算过程再给出结论。
- 每次决策输出JSON, 两种action格式:
  {"actions": [{"entityId": "<必须是entities[]中存在的id>", "shell": "HE"},
               {"bearingDeg": 75.0, "distanceKm": 9.1, "shell": "AP"}], "reason": "..."}
  不开火时输出 {"actions": [], "reason": "..."}
- 队列纪律(最重要): fcs.pendingTasks列出所有待执行任务(若无此字段则以pendingCount计数),
  每个任务执行约需1分钟, 队列会自动逐个打完。目标在pendingTasks/你的决策历史里已有
  未执行完的任务时, 严禁再排——"已下达"不等于"已打完", 你看不到弹着不代表任务丢了。
  补射的唯一条件: 收到该目标明确的未击穿/未命中报告, 且队列中已无针对它的任务。
  已摧毁(isAlive=false)的目标绝不再排。宁可这轮不开火, 也不要堆积队列浪费弹药。
""";

    private const int PollSliceMs = 5_000;
    private const int RecheckAfterSlices = 5; // 5 x 5s = idle re-evaluation cadence

    private readonly AgentBridgeMod _mod;
    private readonly object _gate = new();
    private readonly List<string> _history = new();
    private readonly List<string> _log = new();

    private Thread? _thread;
    private CancellationTokenSource? _cts;

    public FdoAgent(AgentBridgeMod mod) => _mod = mod;

    public bool IsRunning => _thread is { IsAlive: true };
    public string Status { get; private set; } = "stopped";
    public string LastReason { get; private set; } = "";

    public IReadOnlyList<string> LogSnapshot()
    {
        lock (_gate) return _log.ToList();
    }

    public void Start()
    {
        if (IsRunning) return;
        if (string.IsNullOrWhiteSpace(AgentConfig.ApiKey))
        {
            Status = "no ApiKey — set [AgentBridge] ApiKey in UserData\\MelonPreferences.cfg";
            return;
        }
        _cts = new CancellationTokenSource();
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

    private void AppendLog(string text)
    {
        lock (_gate)
        {
            _log.Add($"[{DateTime.Now:HH:mm:ss}] {text}");
            if (_log.Count > 300)
                _log.RemoveRange(0, _log.Count - 300);
        }
    }

    private void Loop(CancellationToken ct)
    {
        long since = 0;
        var idleSlices = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
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

    private void Decide(List<BridgeEvent> events, CancellationToken ct)
    {
        var snapshot = MainThread.Run(() => _mod.BuildSnapshot(), 15_000).GetAwaiter().GetResult();
        snapshot.Markers.Clear(); // bridge-internal mechanics, not intel

        var json = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string historyBlock;
        lock (_gate)
            historyBlock = _history.Count == 0 ? "(无)" : string.Join("\n", _history.TakeLast(10));

        var context =
            "## 新事件\n" + string.Join("\n", events.Select(e => $"[{e.Source}/{e.Type}] {e.Text}")) +
            "\n\n## 你此前的决策(最近10条)\n" + historyBlock +
            "\n\n## 当前战场快照\n" + JsonSerializer.Serialize(snapshot, json);

        Status = "thinking...";
        var reply = LlmClient.Chat(SystemPrompt, context, ct);
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
        AppendLog($"决策: {reason}");

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
            };
            var label = req.EntityId ?? $"{req.BearingDeg:F1}°/{req.DistanceKm:F2}km";
            var result = MainThread.Run(() => _mod.QueueFireMission(req), 15_000).GetAwaiter().GetResult();
            AppendLog($"fire {label} ({req.Shell}) -> {result}");
            lock (_gate) _history.Add($"[{stamp}] fire {label} {req.Shell} -> {result}");
        }
    }
}
