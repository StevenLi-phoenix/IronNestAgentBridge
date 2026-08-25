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
- **开局工作流第一步 = 校准炮塔位置**: 收到统帅部电文的铁巢网格(或可反定位的报告数据)后
  立即set_assumed_turret_position; 依据未到则等待。校准之前不做任何解算、不开火——
  原点错误会让一切诸元作废。阵地转移后同理, 必须先重新校准。
- 遵守统帅部电文中的弹药限制与优先目标指令
- 弹种选择: armour=0的单体目标(步兵/无甲车辆)用HE; armour>=1的单体目标HE大概率
  "未击穿", 用AP。APHE是集群杀伤弹(grouping kill): 穿甲后爆破, 用于多个目标
  聚集在一片区域时一发多杀(如相邻的装甲目标群/装甲与步兵混编群)——看到实体表中
  多个目标方位角/距离彼此接近时优先考虑一发APHE覆盖, 而不是逐个排单发。
  role含Fortification或rawId为supplycash/hostilebunker等工事类=地下/加固目标,
  必须AP。immuneShells非空时严禁选名单内弹种。
  **弹种可用性**: 每个任务的征用台只有部分弹种卡, 见战场状态中的"征用台可购弹种"
  清单——只能从该清单选弹, 清单外的弹种FCS购买必败(fail计数+1白白浪费炮位时间)。
  首选弹种不可用时按用途降级替代(如APHE缺货→AP)。
- 合并打击(一发多杀): 排任务前先算目标间距——多个软目标彼此相距不超过弹药爆炸半径
  (见弹药规格表)时, **一发瞄准目标群的几何中点**(用target坐标点名中点)即可全灭,
  严禁逐个各排一发浪费弹药与炮位(例: 两个步兵组相距0.1km, 一发HE覆盖两者)。
  fire成功回执会列出"爆炸半径可同时覆盖"的目标名单——用它核对合并是否成立,
  没被覆盖到的目标才单独排任务。
- 友军安全: 弹着点爆炸半径内有友军/平民(role含Ally/Spotter/civilian)时fire会拒绝并警告。
  此时优先用offsetKmX/offsetKmY把弹着点向**远离友军一侧**移出爆炸半径(牺牲部分毁伤换
  安全), 或改用爆炸半径更小的弹种; 只有统帅部明确要求贴身支援时才confirmFriendlyFire=true。
- 反炮兵威胁下优先高价值目标
- 战争迷雾: entities[]是当前唯一的已揭示目标清单, 为空就说明没有任何目标被揭示。
  entityId必须一字不差地取自entities[]里实际存在的id, 严禁凭空猜测或编造id。
  未揭示目标只能根据电报情报三角定位后用bearingDeg+distanceKm盲射
  (方位角以炮塔为原点, 正北=0°顺时针; 距离单位km)。
- 定位计算(必须用工具, 严禁手算三角函数——手算漂移是脱靶主因):
  * grid_to_km: 电文网格(如"G6 5:3")转km坐标并给出炮塔到该点的射击诸元
  * solve_target: 观测线/距离圆交汇解算, 返回目标位置(kmX,kmY)。战场报告的
    "自X的方位角B°"是一条line {from:"X的网格", bearingDeg:B}; "自X距离D"是一个
    circle {from:..., distanceKm:D}; "自X方位角B及距离D"是line带distanceKm(直接定位)。
  * 开火: 位置类目标用action的target字段("kmX,kmY"或网格)直接点名——诸元由系统
    在入队时按棋子实时位置推导。firing_solution仅用于人工核对诸元, 不是开火必经步骤。
  你只负责从电文中抄录观测数据和选择组合, 数值计算一律交给工具。
- 盲射精度认知: 情报本身有量化误差(网格±0.05km、方位角±0.5°), 远距离斜交线解算
  误差被放大。盲射=效力侦察(ranging fire): 第一发的价值是炸开迷雾揭示目标。
  弹着揭示目标(entity_revealed事件)后, 立即用entityId对其精确补射, 那才是摧毁手段。
  同一目标若有"方位角+距离"组合优先用它, 且优先选距目标近的观测员的数据。
- 试射修正(registration): shell_impact事件给出**实际弹着点**。与你的预期弹着对比:
  若多发呈现**一致的系统性偏移向量**, 说明假定炮位有误——把偏移向量反向加到当前
  假定炮位上(用solve_target/坐标运算), set_assumed_turret_position修正, 后续所有射击自动归正。
  随机散布(每发偏向不同)则是正常弹道误差, 不要修炮位。
- 弹着修正提示(impact_hint事件, 即地图上的黄色箭头): 脱靶弹着会附带指向附近目标的
  大致方位和距离提示。注意: 方位角有误差(实为一个方向范围), 距离数字也不精确, 且
  误差有多大不可知——两者都严禁当作解算输入。只做定性修正: 下一发沿提示方向、按
  提示距离的量级移动瞄点再试射, 逐发收敛。"弹着确认命中"(无箭头)说明爆炸半径内已有目标。
- 弹药成本(征用点): STAR=2, HE/AP=18。因此侦察性盲射一律用STAR——它的任务是照亮/
  揭示区域, 不是摧毁; 用AP/HE盲射等于花9倍的钱赌一发不准的弹。只有对已揭示目标
  (entityId)才花HE/AP做摧毁性射击。例外: 统帅部明确限制弹种时从其指令。
- 开火: 用 **fire 工具**, 每个目标一次调用, 一轮内可连续多次。目标三选一:
  entityId(逐字来自entities[]) / target(坐标点名, 盲射首选) / bearingDeg+distanceKm。
  坐标(target)优于bearing/distance: 诸元入队时按炮塔棋子实时位置推导, 校准后自动正确。
- 每轮最后用**普通文本**简述决策理由(1-3句): 打了什么/为什么/在等什么。不需要输出任何JSON。
- priority规则(fire工具的priority参数): 反炮兵/敌方炮兵威胁=90以上(FCS跳过凑单等待
  立即抢占下一门空炮); 统帅部点名的优先目标=70; 常规高价值(仓库/工事/指挥所)=60;
  普通目标=50; 低价值步兵/补刀=30。FCS的matcher按优先级分配炮位, 把发现的目标都排上、
  优先级排对即可; 高优任务随时插队。已入队任务不会因目标死亡自动取消,
  排队前确认isAlive, 死目标的排队任务用cancel_pending_task清掉。
- 队列纪律(最重要): **队列状态的唯一权威是当前快照的 fcs.pendingTasks + L/R 炮位任务**,
  实时反映事实。你的对话历史只说明"下达过", 不说明"还在队列":
  * 目标出现在 pendingTasks 或 L/R 上 → 在途, 严禁重复排。
  * 历史称已排、但 pendingTasks 和炮位上都没有 → 该任务已执行完毕(弹已出膛)或被
    F9/取消清除, "在队列中"的说法是错误的, 不要这么表述。此时看目标:
    isAlive=false → 已解决; 仍alive → 弹着可能未命中或任务被清, **可以重新排**
    (这不算重复——队列里已经没有它了)。
  * F9/重置后队列清空, 历史里所有"已排"作废, 以快照为准重新规划。
  排队延迟认知: 任务上炮后执行约1分钟, 但双炮吞吐有限, 队列深时可等15分钟以上——
  队列越深越要克制, 低优先级目标宁可不排; 排队久的目标可能已移动/被摧毁。
  已摧毁(isAlive=false)的目标绝不排。宁可这轮不开火, 也不要堆积队列浪费弹药。
""";

    private const string ToolsJson = """
[
  {
    "type": "function",
    "function": {
      "name": "grid_to_km",
      "description": "把电文网格坐标(如'G6 5:3')转换为km坐标(仅位置, 不含诸元)",
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
      "name": "set_assumed_turret_position",
      "description": "把指挥桌上的炮塔棋子移动到指定位置。FCS与所有解算以棋子位置为射击原点。合法校准依据: (1)统帅部电文中的铁巢网格('铁巢 - [GRID]'或阵地转移宣告的新网格); (2)战场/侦查报告中可反解算出炮位的观测数据(先用solve_target解出炮位坐标)。两者都没有时**禁止调用本工具**——保持未校准等待, 绝不猜测坐标。",
      "parameters": {
        "type": "object",
        "properties": { "position": { "type": "string", "description": "网格如'H2 3:4'或km坐标'7.35,1.45'" } },
        "required": ["position"]
      }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "fire",
      "description": "排一个火力任务(FCS自动完成购弹/装填/瞄准)。目标三选一: entityId(必须逐字来自entities[]); target(坐标点名, 网格'K4 5:0'或'kmX,kmY', 盲射首选, 诸元入队时按棋子实时位置推导); bearingDeg+distanceKm(显式诸元)。立即返回排队结果。**注意友军**: 开火前核对弹着点周边——落点在友军/平民(role含Ally/Spotter/civilian)的弹药爆炸半径内即构成误伤。",
      "parameters": {
        "type": "object",
        "properties": {
          "entityId": { "type": "string" },
          "target": { "type": "string" },
          "bearingDeg": { "type": "number" },
          "distanceKm": { "type": "number" },
          "shell": { "type": "string", "description": "弹种, 从征用台清单选" },
          "priority": { "type": "number", "description": "0-100, 默认50; 反炮兵>=90" },
          "offsetKmX": { "type": "number", "description": "弹着点微偏移km(东正西负, |≤0.5|): 在选定目标基础上把弹着点移开, 用于避开近旁友军(向远离友军方向偏)或瞄准目标群中点" },
          "offsetKmY": { "type": "number", "description": "弹着点微偏移km(北正南负, |≤0.5|)" },
          "confirmFriendlyFire": { "type": "boolean", "description": "友军在爆炸半径内时fire会拒绝并警告; 仅在确认接受误伤风险时置true重试" }
        },
        "required": ["shell"]
      }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "firing_solution",
      "description": "对指定目标点计算射击诸元(方位角/距离), 以炮塔棋子的**当前实时位置**为原点。给target(网格或km坐标)或entityId二选一。开火前、尤其是棋子刚被移动/校准后, 用它取最新诸元。",
      "parameters": {
        "type": "object",
        "properties": {
          "target": { "type": "string", "description": "目标: 网格'G6 5:3'或'kmX,kmY'" },
          "entityId": { "type": "string", "description": "或: entities[]中的实体id" }
        }
      }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "get_assumed_turret_position",
      "description": "查询**当前假定的**炮塔位置(=指挥桌棋子的位置, 不是ground truth)。返回km坐标+网格。",
      "parameters": { "type": "object", "properties": {} }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "cancel_pending_task",
      "description": "取消FCS等待队列中的一个任务(按T编号, 见'FCS待执行'清单; 每次取消队列中第一个匹配项)。已在左右炮上执行中的任务无法取消(高优先级任务的抢占机制会处理)。用于: 目标已被摧毁但任务还在排队、弹种排错、或需要给队列腾位。",
      "parameters": {
        "type": "object",
        "properties": { "targetId": { "type": "number", "description": "任务的T编号" } },
        "required": ["targetId"]
      }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "requisition_card",
      "description": "向FCS控制台协调器提交打孔卡购买请求(串行执行: 插卡/设旋钮/购买, 结果经事件回报)。用于非弹药类卡片; 弹药购买由FCS自动完成, 不要用本工具买弹。侦察机卡(如ScoutPlane)给bearingDeg指定侦查方向。特殊卡价格见清单, 侦察机很贵, 只在情报价值明确时使用。priority: 普通卡50; 紧急类卡(如'紧急转移'EmergencyRelocation)=100立即插队优先执行。",
      "parameters": {
        "type": "object",
        "properties": {
          "cardId": { "type": "string", "description": "卡片ID, 见征用台可购清单" },
          "bearingDeg": { "type": "number", "description": "侦察类卡: 侦查飞行方向方位角(北=0顺时针)" },
          "startGrid": { "type": "string", "description": "侦察类卡: 起飞网格单元(如'P4')——飞机从此格沿bearingDeg方向飞行揭雾, 必须与bearing一起规划成想要的航线" },
          "priority": { "type": "number", "description": "0-100, 默认50; 紧急转移类=100" }
        },
        "required": ["cardId"]
      }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "solve_target",
      "description": "由观测线/距离圆精确解算目标位置, 返回km坐标与网格(仅位置)。所有三角定位必须用本工具。开火时把返回的kmX,kmY直接填进action的target字段('kmX,kmY'), 不需要自己算诸元。",
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
    private const int RecheckAfterSlices = 12; // 12 x 5s = 60s idle re-evaluation cadence
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

    public enum AgentState { Stopped, Running, Paused, Stopping }

    public bool IsRunning => _thread is { IsAlive: true };
    public AgentState State { get; private set; } = AgentState.Stopped;
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
        State = AgentState.Running;
        Status = "running";
        AppendLog("agent started");
    }

    public void Stop()
    {
        _cts?.Cancel();
        // The loop thread may still be mid-LLM-round; it flips to Stopped on exit.
        State = IsRunning ? AgentState.Stopping : AgentState.Stopped;
        Status = IsRunning ? "stopping (finishing current round)" : "stopped";
        AppendLog("agent stop requested");
    }

    public void ClearLog()
    {
        lock (_gate) { _log.Clear(); _history.Clear(); _recentToolCalls.Clear(); }
        StreamingText = "";
        LastReason = "";
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

        try
        {
            LoopBody(ct, ref since, ref idleSlices);
        }
        finally
        {
            State = AgentState.Stopped;
            Status = "stopped";
        }
    }

    private void LoopBody(CancellationToken ct, ref long since, ref int idleSlices)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Mirror FCS's focus gate: no decisions (and no token spend) while the game
                // is in the background; FCS pauses its automation there anyway.
                if (!AgentBridgeMod.GameFocused || AgentBridgeMod.CinematicActive)
                {
                    State = AgentState.Paused;
                    Status = AgentBridgeMod.CinematicActive ? "paused (cinematic)" : "paused (game unfocused)";
                    if (ct.WaitHandle.WaitOne(1_000)) break;
                    continue;
                }
                if (State == AgentState.Paused)
                {
                    State = AgentState.Running;
                    Status = "running";
                }

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
        // Never print the turret coordinate here — the agent's knowledge of its own
        // position must come from the wire + its own calibration, or it echoes whatever
        // value the system shows (observed failure mode). Calibration is tracked as an
        // ACT this mission (tool call or detected manual drag), not inferred from position.
        sb.AppendLine(s.TurretCalibrated
            ? "炮塔棋子: 已校准(如需查询假定位置用get_assumed_turret_position)"
            : "炮塔棋子: ⚠本局尚未校准! 出生默认位置不可信, 校准前实体方位/距离均不可信。"
              + "合法校准依据=统帅部电文中的铁巢网格, 或战场/侦查报告中可解算出炮位的观测数据(用solve_target反定位); "
              + "**两者都没有就保持未校准并等待, 绝不猜测/编造坐标**");
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
        var shellNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AP","APHE","ATMC","CLMN","CYAN","DRIL","EQKE","FLCH","HCHE","HE",
            "INCN","LE","PLCM","PCLM","PHGN","PRPG","SMK","STAR","TEAR","THRM","WP",
        };
        string CardLabel(CardDto c) => $"{c.Id}({c.Cost}点{(c.RemainingUses > 0 ? $", 余{c.RemainingUses}次" : "")})";
        var shells = s.Cards.Where(x => shellNames.Contains(x.Id)).ToList();
        var specials = s.Cards.Where(x => !shellNames.Contains(x.Id)).ToList();
        sb.AppendLine("征用台可购弹种及单价(开火只能从此选, 清单外弹种购买必败): "
                      + (shells.Count == 0 ? "(未就绪)" : string.Join(", ", shells.Select(CardLabel))));
        if (specials.Count > 0)
            sb.AppendLine("征用台特殊卡及单价(仅经requisition_card工具使用, 不是弹种, 注意贵价卡值不值得花): "
                          + string.Join(", ", specials.Select(CardLabel)));
        if (s.ShellSpecs.Count > 0)
        {
            sb.AppendLine("弹药规格(爆炸半径决定覆盖/友军安全距离; 射程按装药档):");
            foreach (var spec in s.ShellSpecs)
            {
                var ranges = spec.ChargeRanges.Count > 0
                    ? string.Join(" ", spec.ChargeRanges.OrderBy(c => c.Charge).Select(c => $"C{c.Charge}:{c.MinKm:F1}-{c.MaxKm:F1}km"))
                    : "射程表未知";
                sb.AppendLine($"  {spec.Id}: 爆半径{spec.ImpactRadius:F0}m 伤害{spec.Damage}"
                              + (spec.ProjectilesPerShell > 1 ? $"×{spec.ProjectilesPerShell}弹" : "")
                              + $" {ranges}");
            }
        }
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

    /// <summary>
    /// Draw the plotting work on the tactical map with the player's own tools:
    /// yellow pen for observation lines, compass for range circles, a pen dot
    /// (zero-length marker) at the solved intersection.
    /// </summary>
    private static void PlotGeometry(GridMath.SolveGeometry geometry)
    {
        foreach (var (from, to) in geometry.Lines)
            GameState.MapDrawer.Draw(0, "MapMarkerYellow",
                new UnityEngine.Vector2((float)from.x, (float)from.y),
                new UnityEngine.Vector2((float)to.x, (float)to.y));
        foreach (var (center, radius) in geometry.Circles)
            GameState.MapDrawer.Draw(0, "MapMarkerDiscCompass",
                new UnityEngine.Vector2((float)center.x, (float)center.y),
                new UnityEngine.Vector2((float)(center.x + radius), (float)center.y));
        if (geometry.Solution is { } s)
            GameState.MapDrawer.Draw(0, "MapMarkerRED",
                new UnityEngine.Vector2((float)s.x, (float)s.y),
                new UnityEngine.Vector2((float)s.x, (float)s.y));
    }

    private string ExecuteFiringSolution(JsonElement args)
    {
        var local = MainThread.Run(() => _mod.ReadTurretLocal(), 10_000).GetAwaiter().GetResult();
        var turret = (x: (double)(MapOffsetX + local.x * MapLocalToKm), y: (double)(MapOffsetY + local.y * MapLocalToKm));

        (double x, double y)? point = null;
        string label;
        if (args.TryGetProperty("entityId", out var e) && e.GetString() is { Length: > 0 } entityId)
        {
            var entity = MainThread.Run(() => _mod.FindVisibleEntity(entityId), 10_000).GetAwaiter().GetResult();
            if (entity == null)
                return JsonSerializer.Serialize(new { error = $"entity '{entityId}' not visible on the map" });
            point = (MapOffsetX + entity.MapX * MapLocalToKm, MapOffsetY + entity.MapY * MapLocalToKm);
            label = entityId;
        }
        else if (args.TryGetProperty("target", out var t) && t.GetString() is { Length: > 0 } target)
        {
            point = GridMath.ParsePoint(target, turret);
            if (point == null)
                return JsonSerializer.Serialize(new { error = $"cannot parse target '{target}'" });
            label = target;
        }
        else
        {
            return JsonSerializer.Serialize(new { error = "need target or entityId" });
        }

        var p = point.Value;
        var dx = p.x - turret.x;
        var dy = p.y - turret.y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        var bearing = Math.Atan2(dx, dy) * 180.0 / Math.PI;
        if (bearing < 0) bearing += 360;
        return JsonSerializer.Serialize(new
        {
            target = label,
            bearingDeg = Math.Round(bearing, 2),
            distanceKm = Math.Round(dist, 3),
            turretKm = new { x = Math.Round(turret.x, 3), y = Math.Round(turret.y, 3) },
            inMapBounds = GridMath.InMapBounds(p),
        });
    }

    private string ExecuteGetTurret()
    {
        var local = MainThread.Run(() => _mod.ReadTurretLocal(), 10_000).GetAwaiter().GetResult();
        var kmX = MapOffsetX + local.x * MapLocalToKm;
        var kmY = MapOffsetY + local.y * MapLocalToKm;
        var col = (int)kmX is >= 0 and < 26 ? ((char)('A' + (int)kmX)).ToString() : "#";
        var grid = $"{col}{(int)kmY + 1} {(int)(kmX * 10) % 10}:{(int)(kmY * 10) % 10}";

        if (!GridMath.InMapBounds((kmX, kmY)))
            return JsonSerializer.Serialize(new
            {
                unreliable = true,
                note = "假定炮塔位置在地图之外, 不可信。用其他信息(统帅部电文的铁巢网格/侦查报告反定位)重新set_assumed_turret_position。",
            });

        return JsonSerializer.Serialize(new
        {
            kmX = Math.Round(kmX, 3),
            kmY = Math.Round(kmY, 3),
            grid,
        });
    }

    private string ExecuteCancelPending(JsonElement args)
    {
        if (!args.TryGetProperty("targetId", out var t) || t.ValueKind != JsonValueKind.Number)
            return JsonSerializer.Serialize(new { error = "targetId required" });
        var id = t.GetInt32();
        var result = MainThread.Run(() => _mod.CancelPendingFcsTask(id), 15_000).GetAwaiter().GetResult();
        AppendLog($"cancel T{id} -> {result}", "cancel", new { targetId = id, result });
        return JsonSerializer.Serialize(new { result });
    }

    private string ExecuteSetTurret(JsonElement args, (double x, double y) turretKm)
    {
        var pos = args.TryGetProperty("position", out var p) ? p.GetString() ?? "" : "";
        if (GridMath.ParsePoint(pos, turretKm) is not { } km)
            return JsonSerializer.Serialize(new { error = $"cannot parse position '{pos}' (grid like 'H2 3:4' or 'kmX,kmY')" });
        var result = MainThread.Run(() => _mod.SetDeclaredTurret((float)km.x, (float)km.y), 15_000).GetAwaiter().GetResult();
        AppendLog($"turret declared at km({km.x:F2},{km.y:F2})", "turret", new { km.x, km.y });
        return JsonSerializer.Serialize(new { result });
    }

    private string ExecuteRequisition(JsonElement args, StateSnapshotDto snapshot)
    {
        var cardId = args.TryGetProperty("cardId", out var c) ? c.GetString() ?? "" : "";
        if (cardId.Length == 0)
            return JsonSerializer.Serialize(new { error = "cardId required" });
        float? bearing = args.TryGetProperty("bearingDeg", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetSingle() : null;
        var cardPriority = args.TryGetProperty("priority", out var pr) && pr.ValueKind == JsonValueKind.Number
            ? Math.Clamp(pr.GetInt32(), 0, 100) : 50;
        var startGrid = args.TryGetProperty("startGrid", out var sg) ? sg.GetString() : null;
        // Preferred path: a DTO into FCS's own console coordinator (serialized with its
        // auto-buys). Legacy bridge-side physical routine only for stock FCS.
        var result = MainThread.Run(() => _mod.RequestCard(cardId, bearing, cardPriority, startGrid), 15_000)
            .GetAwaiter().GetResult();
        return JsonSerializer.Serialize(new { result });
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
            string SolveAndPlot(JsonElement a)
            {
                var json = GridMath.SolveTarget(a, turretKm, out var geometry);
                if (geometry.Solution != null)
                    MainThread.Post(() => PlotGeometry(geometry)); // cosmetic — never blocks the agent
                return json;
            }

            var result = name switch
            {
                "grid_to_km" => GridMath.GridToKm(args, turretKm),
                "solve_target" => SolveAndPlot(args),
                "requisition_card" => ExecuteRequisition(args, snapshot),
                "set_assumed_turret_position" or "set_turret_position" => ExecuteSetTurret(args, turretKm),
                "cancel_pending_task" => ExecuteCancelPending(args),
                "get_assumed_turret_position" or "get_turret_position" => ExecuteGetTurret(),
                "firing_solution" => ExecuteFiringSolution(args),
                "fire" => ExecuteFire(args),
                // Legacy hallucination shape {"actions":[...]} — execute each as a fire call.
                _ when args.TryGetProperty("actions", out var acts) && acts.ValueKind == JsonValueKind.Array
                    => ExecuteFireBatch(acts),
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
        _firesThisRound = 0;
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

        // Fire happens through the fire tool during the round; the final text IS the reason.
        var reason = reply.Trim();
        if (reason.Length > 500)
            reason = reason[..500] + "…";
        LastReason = reason;
        AppendLog($"决策: {reason}", "decision",
            new { events = events.Select(e => $"{e.Source}/{e.Type}").ToList(), fires = _firesThisRound });

        if (_firesThisRound == 0)
        {
            var brief = reason.Length > 120 ? reason[..120] + "…" : reason;
            lock (_gate) _history.Add($"[{DateTime.Now:HH:mm:ss}] 无行动: {brief}");
        }
    }

    private int _firesThisRound;

    /// <summary>The fire tool: one mission per call, executed immediately during the round.</summary>
    private string ExecuteFire(JsonElement action)
    {
        var req = new FireMissionRequest
        {
            EntityId = action.TryGetProperty("entityId", out var id) ? id.GetString() : null,
            TargetPoint = action.TryGetProperty("target", out var tp) ? tp.GetString() : null,
            BearingDeg = action.TryGetProperty("bearingDeg", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetSingle() : null,
            DistanceKm = action.TryGetProperty("distanceKm", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetSingle() : null,
            Shell = action.TryGetProperty("shell", out var s) ? s.GetString() ?? "HE" : "HE",
            Priority = action.TryGetProperty("priority", out var p) && p.ValueKind == JsonValueKind.Number ? Math.Clamp(p.GetInt32(), 0, 100) : 50,
            OffsetKmX = action.TryGetProperty("offsetKmX", out var ox) && ox.ValueKind == JsonValueKind.Number ? ox.GetSingle() : null,
            OffsetKmY = action.TryGetProperty("offsetKmY", out var oy) && oy.ValueKind == JsonValueKind.Number ? oy.GetSingle() : null,
            ConfirmFriendlyFire = action.TryGetProperty("confirmFriendlyFire", out var cff) && cff.ValueKind == JsonValueKind.True,
        };
        var label = req.EntityId ?? req.TargetPoint ?? $"{req.BearingDeg:F1}°/{req.DistanceKm:F2}km";
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        _firesThisRound++;

        if (AgentConfig.PriorityQueue)
        {
            _mod.MissionQueue.Add(req, req.Priority, label);
            AppendLog($"staged P{req.Priority} {label} ({req.Shell})", "staged", req);
            lock (_gate) _history.Add($"[{stamp}] staged P{req.Priority} {label} {req.Shell}");
            return JsonSerializer.Serialize(new { result = $"staged P{req.Priority}" });
        }

        var result = MainThread.Run(() => _mod.QueueFireMission(req), 15_000).GetAwaiter().GetResult();
        AppendLog($"fire {label} ({req.Shell}, P{req.Priority}) -> {result}", "fire", new { req, result });
        lock (_gate) _history.Add($"[{stamp}] fire {label} {req.Shell} -> {result}");
        return JsonSerializer.Serialize(new { result });
    }

    private string ExecuteFireBatch(JsonElement actions)
    {
        var results = new List<string>();
        foreach (var action in actions.EnumerateArray())
            results.Add(ExecuteFire(action));
        return JsonSerializer.Serialize(new { results });
    }
}
