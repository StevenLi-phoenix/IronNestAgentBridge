# 模块 agent-loop（内置 FDO Agent 主循环）

来源文件：`C:/Users/stevenli/Codes/IronNestAgentBridge/agent/FdoAgent.cs`（共 1180 行）。
本模块是 mod 内进程的"射击指挥官"决策体：后台线程跑事件驱动的 LLM 多轮对话，
通过工具调用把决策落到 FCS。它**不做**任何游戏对象访问，全部经 `MainThread` 转交主线程。

---

## 1. 逐字保留数据块（重实现时原样搬运，禁止改写/重新翻译/重新排版）

以下四块是"协议内容"而非代码，必须字节级搬运（含全角/半角标点、`**`加粗记号、
换行位置、缩进空格）。任何改写都会改变 agent 的行为，且部分内容是实测得出的学说，
无法从代码重新推导。

| 数据块 | 文件:行 | 说明 |
|---|---|---|
| `SystemPrompt` 学说全文 | `agent/FdoAgent.cs:13-238`（字符串内容 14-237） | 唯一的 system message；含权威层级、弹种学说、队列纪律、待命条令、网格方向、盲射/侦察规则等 |
| `ToolsJson` 工具定义（含每个工具的中文 description） | `agent/FdoAgent.cs:240-453`（JSON 内容 241-452） | OpenAI function-calling 格式数组；**工具名/参数名/required 是协议**（见 §6 逐条列出），description 正文属自然语言数据块 |
| `MapIntelTable` 关卡情报表 | `agent/FdoAgent.cs:655-680`（条目 657-679） | 3 条：`"白色炮弹"`、`"敌人如潮"`、`"最终收割"` |
| 压缩接班简报提示词 | `agent/FdoAgent.cs:779-781` | 见 §8，短，已在本文内联 |

`BuildCompactState` 内嵌的固定中文串（作战模式释义、未校准警告、各段落表头）见 §5，
那些属于快照文本协议，本文已逐字记录，无需另行搬运源码。

---

## 2. 生命周期与状态机

### 2.1 对外状态
- `enum AgentState { Stopped, Running, Paused, Stopping }`。
- `State`（AgentState）、`Status`（人类可读字符串）、`LastReason`（最近一轮决策文本）、
  `StreamingText`（本轮流式输出的实时累积，空闲时为空串）、`IsStreaming`（bool）。
- `IsRunning` 必须等价于"后台线程存在且存活"。
- 这些字段由 agent 线程写、由 Unity 主线程（UI 每帧）读，**不加锁**；因此必须是不可变值的
  简单赋值（string/bool/enum），禁止暴露可变集合。

### 2.2 `Start()`
按顺序做守卫，任一失败即返回且**不启动线程**：
1. `IsRunning` 为真 → 直接返回（幂等，不改 Status）。
2. `AgentConfig.LlmControl == false` → `Status = "LLM control disabled"`，返回。
3. `AgentConfig.ApiKey` 为空/空白 → `Status = "no ApiKey — set [AgentBridge] ApiKey in UserData\MelonPreferences.cfg"`，返回。

通过后：新建 `CancellationTokenSource`；**清空消息历史**并只放入一条
`{ role: "system", content: SystemPrompt }`；清空 `_carrySummary`；
启动线程（`IsBackground = true`，`Name = "AgentBridge-FDO"`）；
`State = Running`；`Status = "running"`；`AppendLog("agent started")`。

> 每次 Start 都是全新对话：不恢复上次会话的历史，也不恢复上次的接班简报。

### 2.3 `Stop()`
取消 CTS；`State = IsRunning ? Stopping : Stopped`；
`Status = IsRunning ? "stopping (finishing current round)" : "stopped"`；
`AppendLog("agent stop requested")`。
**Stop 不阻塞、不 join 线程**——线程可能正处在 LLM 轮中，它自己退出时在 `finally` 里
把 `State = Stopped`、`Status = "stopped"`。

### 2.4 `ClearLog()`
清空日志、动作历史、最近工具调用三个列表（持 `_gate` 锁），并把 `StreamingText` 与
`LastReason` 置空串。**不影响** `_messages` 对话历史（对话由 Start 重建）。

### 2.5 线程主体
进入时 `_eventCursor = 0`、`idleSlices = 0`；无论如何退出，`finally` 必须置
`State = Stopped; Status = "stopped"`。

---

## 3. 主循环（事件驱动 + 防抖 + 空转退避）

循环体在 `!ct.IsCancellationRequested` 下反复执行，整个循环体包在 try/catch 内：

### 3.1 暂停门（每轮最先判定）
若 `AgentBridgeMod.GameFocused == false` **或** `AgentBridgeMod.CinematicActive == true`：
- `State = Paused`；
- `Status = CinematicActive ? "paused (cinematic)" : "paused (game unfocused)"`
  （**过场动画优先于失焦**判定文案）；
- 等待 **1000 ms**（可被取消打断，被打断则退出循环）；
- `continue`。

理由（必须保留的语义）：镜像 FCS 的焦点门——游戏在后台时 FCS 自身也暂停自动化，
此时决策既无意义又白烧 token。

从 Paused 恢复时（本轮未命中暂停门且 `State == Paused`）→ `State = Running; Status = "running"`。

> 暂停期间 `_eventCursor` 不推进，恢复后积压事件会一次性全部投递。

### 3.2 取事件
`EventLog.WaitForEvents(_eventCursor, PollSliceMs)`，`PollSliceMs = 5000`。
调用返回后若已取消 → 退出循环。

**有事件时**：
1. `_eventCursor = events[^1].Seq`；`idleSlices = 0`；`_idleRechecks = 0`（真实事件终止一切退避）。
2. **防抖**：突发事件在一两秒内陆续到达（电报逐行打印、多实体同时揭示）。
   继续以 **1000 ms** 为片反复拉取，直到某次拉到 0 条为止；
   总窗口硬上限 **6000 ms**（`settleDeadline = 现在 + 6000`）。
   每次拉到的都追加进本批并推进游标。目的：一次决策看到完整画面，而不是每个碎片一轮。
3. **批内去重**：去重键 = `e.Type + "" + e.Text`（字符串拼接，无分隔符），
   保留首次出现、保持原顺序。理由：同 type+text 的重复只增 token 不增情报。

**无事件时（空转退避）**：
- 阈值 `threshold = RecheckAfterSlices * Math.Min(8, 1 << Math.Min(3, _idleRechecks))`，
  其中 `RecheckAfterSlices = 12`（12 × 5s = 60s）。
  即连续空转的复查间隔为 **60s → 120s → 240s → 480s（封顶）**。
- `if (++idleSlices < threshold) continue;`
- 达阈值：`idleSlices = 0`；`_idleRechecks++`；
  构造**唯一一条合成事件**投入决策：
  - `Source = "agent"`
  - `Type = "recheck"`
  - `Text = "定时复查: 无新事件, 重新评估当前战场态势"`
  - `GameTime = EventLog.GameClock`
- 理由：叙事/无目标的收尾关否则每分钟白烧一轮；任何真实事件重置节奏。

### 3.3 决策
调用 `Decide(events, ct)`（§4）。

### 3.4 错误处理
- `OperationCanceledException` → 直接退出循环（不记错误）。
- 其他 `Exception ex` → `Status = $"error: {ex.Message}"`；
  `AppendLog($"error: {ex.Message}")`；等待 **5000 ms**（可取消，被取消则退出）；
  然后 `Status = "running"`，继续下一轮。
  **绝不让异常杀死线程**——一次 LLM 超时/反射失败不能终结指挥官。

---

## 4. 一轮决策 `Decide(events, ct)`

### 4.1 取快照
`MainThread.Run(() => _mod.BuildSnapshot(), 15_000)` 同步等待。

### 4.2 组装本轮 user 消息（严格按此顺序拼接）
```
[若 _carrySummary 非空] "## 前情简报(此前对话已压缩)\n" + _carrySummary + "\n\n"
"## 新事件(带游戏内任务计时)\n" + 事件行（\n 连接）
"\n\n"
BuildCompactState(snapshot)
```
拼完后 **`_carrySummary` 立即清空**（接班简报只注入一次）。

**事件渲染格式（唯一权威）**：
`"[" + (GameTime 非空 ? GameTime + " " : "") + Source + "/" + Type + "] " + Text`
例：`[07:42 primary/telegraph_message] ...`

### 4.3 本轮炮塔参考点
```
turretKm.x = MapOffsetX + snapshot.TurretMapX * MapLocalToKm
turretKm.y = MapOffsetY + snapshot.TurretMapY * MapLocalToKm
```
常量：`MapLocalToKm = 3.8164`、`MapOffsetX = 10.016`、`MapOffsetY = 5.235`。

这个 `turretKm` 在**整轮内冻结**，被 `grid_to_km`、`solve_target`、`distance_between`、
`entities_near`、`set_assumed_turret_position` 的坐标解析共用；
而 `firing_solution`、`get_assumed_turret_position` 每次**重新读实时炮塔位置**。
（这是刻意的不对称，见 §11 不变量与 §12 疑问。）

### 4.4 压缩检查（在追加 user 消息**之前**）
```
if (UsageMeter.LastPromptTokens > CompactAtPromptTokens && _messages.Count > 3)
    CompactConversation(ct);
```
`CompactAtPromptTokens = 400_000`（long）。见 §8。

### 4.5 发起 LLM 轮
1. 把组装好的 context 作为 `{ role: "user" }` 追加进 `_messages`。
2. `Status = "thinking..."`；`IsStreaming = true`；`StreamingText = ""`；`_firesThisRound = 0`。
3. `LlmClient.ChatStream(_messages, ToolsJson, ExecuteTool, chunk => { 累加到 buffer; StreamingText = buffer.ToString(); }, ct)`。
4. `finally { IsStreaming = false; }`（异常路径也必须复位）。
5. `Status = "running"`。

**对话必须持久且前缀字节稳定**：system + 每一轮（含所有工具轮的 assistant/tool 消息）
原样留在 `_messages` 里，跨决策不重排、不删改，以命中服务端前缀缓存。
`_messages` 由本模块持有，`LlmClient` 就地追加 assistant/tool 轮。

### 4.6 收尾
- `reason = reply.Trim()`；若长度 > **500** → 截断为前 500 字符 + `"…"`；写入 `LastReason`。
- `AppendLog($"决策: {reason}", "decision", new { events = 事件的 "Source/Type" 列表, fires = _firesThisRound })`。
- 若 `_firesThisRound == 0`：把 `reason` 再截到 **120** 字符（超出加 `"…"`），
  向 `_history` 追加 `$"[{DateTime.Now:HH:mm:ss}] 无行动: {brief}"`。

> 最终文本本身**就是**决策理由（SystemPrompt 要求 1-3 句普通文本、不输出 JSON）；
> 开火发生在轮内的 fire 工具调用中，不在最终文本里解析任何动作。

---

## 5. 快照文本协议 `BuildCompactState(StateSnapshotDto s)`

逐行拼装（`AppendLine`）。以下每一行的措辞都是协议，SystemPrompt 与其对齐，改动即失配。

1. **表头**：`s.GameTime` 非空 → `## 战场状态 @ {GameTime} (任务时钟)`；否则 `## 战场状态`。
2. **绝不打印炮塔坐标**（见 §11 不变量 I1）。
3. **作战模式**（`s.MissionType` 非空时）：`"作战模式: " + ` 映射
   - `"Chill"` → `无尽模式(Chill)——敌军无限补充; 摧毁敌炮只延长反炮击倒计时, 不能根治`
   - `"Challange"` → `无尽模式(Challenging)——敌军无限补充; 摧毁敌炮只延长反炮击倒计时, 不能根治`
     （键的拼写 `Challange` 是游戏侧枚举原样，**不得纠正**）
   - `"Campaign"` → `剧本任务——敌军编制有限; 敌炮全灭=反炮击倒计时彻底停止`
   - `"Tutorial"` → `教程关`
   - 其他 `other` → `未知类型 '{other}' (按剧本任务处置)`
4. **关卡与关卡情报**（`s.MissionName` 非空时）：
   - `当前关卡: {MissionName}`
   - 遍历 `MapIntelTable`，凡 `MissionName.Contains(Key, StringComparison.OrdinalIgnoreCase)`
     命中，追加一行 `关卡情报(指挥官提供, 优先于通用学说): {Intel}`。
     **命中多条则全部追加**（不是首个命中即停）。
   - 表的 Key 必须用**游戏显示语言**书写（子串匹配本地化后的关卡名）。
5. **地图范围**（`s.MapExtentKm` 非空时）：
   `本关地图实测范围: {MapExtentKm} — 瞄准点出界会被fire拒绝; 规划盲射/侦察航线前先对照此范围`
6. **炮塔棋子行**（二选一，逐字）：
   - 已校准：`炮塔棋子: 已校准(如需查询假定位置用get_assumed_turret_position)`
   - 未校准：`炮塔棋子: ⚠本局尚未校准! 出生默认位置不可信, 校准前实体方位/距离均不可信。合法校准依据=统帅部电文中的铁巢网格, 或战场/侦查报告中可解算出炮位的观测数据(用solve_target反定位); **两者都没有就保持未校准并等待, 绝不猜测/编造坐标**`
     （源码里由三段字符串拼接，最终是**一行**，无换行）
7. **FCS 汇总行**：
   `FCS: pending={PendingCount} done={CompletedTaskCount} fail={FailedTaskCount} | T9(左炮): {LeftTask ?? "-"} | T10(右炮): {RightTask ?? "-"}`
8. **待执行队列**（`PendingTasks.Count > 0` 时）：
   - 表头：`FCS待执行(#N=任务唯一编号, adjust/cancel用它; 排列=计划炮击顺序: 优先级带内按方位就近连打):`
   - 每项前缀两个空格：`"  " + t`（`t` 是 FCS 侧已格式化的任务串，原样透传）
9. **在途炮弹**（`InFlightShells.Count > 0` 时）：
   `在途炮弹(已出膛未落地, 目标已被服务, **严禁重复排队**): ` + 各项以 `" | "` 连接
10. **火炮行**（每门一行）：
    `火炮{Side}: 膛={ChamberedShell ?? "空"} 药={PowderCharges} canFire={CanFire}`
    **刻意不打印** `IsReloading` / `CurrentElevation`（学说明确要求 agent 不看装填状态排任务）。
11. **卡片分类**：弹种名白名单（`StringComparer.OrdinalIgnoreCase` 的 HashSet），逐字：
    `AP, APHE, ATMC, CLMN, CYAN, DRIL, EQKE, FLCH, HCHE, HE, INCN, LE, PLCM, PCLM, PHGN, PRPG, SMK, STAR, TEAR, THRM, WP`
    （`PLCM` 与 `PCLM` **两个拼法都在表内**，容忍游戏侧命名不一致）。
    在此表内 → 弹种；不在 → 特殊卡。
    卡片标签：`{Id}({Cost}点{RemainingUses > 0 ? $", 余{RemainingUses}次" : ""})`
12. **征用点余额**（`RequisitionPoints` 有值时）：
    `征用点余额: {pts}点(每次购买实时扣减, 买不起的方案不要排)`
13. **可购弹种行（无条件输出）**：
    `征用台可购弹种及单价(开火只能从此选, 清单外弹种购买必败): ` + （无弹种时 `(未就绪)`，否则 `", "` 连接的卡片标签）
14. **特殊卡行**（有特殊卡时）：
    `征用台特殊卡及单价(仅经requisition_card工具使用, 不是弹种, 注意贵价卡值不值得花): ` + `", "` 连接
15. **弹药规格**（`ShellSpecs.Count > 0` 时）：
    - 表头：`弹药规格(爆炸半径决定覆盖/友军安全距离; 射程按装药档):`
    - 每条：`  {Id}: 爆半径{ImpactRadius * 1000f:F0}m 伤害{Damage}` +
      （`ProjectilesPerShell > 1` 时追加 `×{ProjectilesPerShell}弹`）+ `" "` + 射程表
    - 射程表：`ChargeRanges` 按 `Charge` 升序，各项 `C{Charge}:{MinKm:F1}-{MaxKm:F1}km`，以**空格**连接；
      `ChargeRanges` 为空时用字面量 `射程表未知`。
    - **`ImpactRadius` 的单位是 km，必须 ×1000 转米**（历史事故：按米处理导致显示"爆半径0m"）。
16. **实体表**：
    - 表头：`可见实体(entityId必须逐字取自此表):`
    - 空表：`  (无 — 没有任何目标被揭示)`（注意破折号前后各一空格）
    - 每条：`  {Id} | {Role} | 甲{Armour} | {Health}/{MaxHealth} | {IsAlive ? "alive" : "DEAD"} | {BearingDeg:F1}° | {DistanceKm:F2}km`
      `ImmuneShells` 非空时追加 ` | 免疫:` + `","` 连接的弹种名。

**未进入快照的 DTO 字段**（刻意或遗留）：`Markers`、`Teleprinters`、`AvailableShells`、
`SceneName`、`SceneBound`、`Timestamp`、`Fcs.AutoFireEnabled/MaxChargeEnabled/SerialToMarker/RecentOutcomes`、
实体的 `RawId/RoleValue/State/StateValue/Stars/Visible/MapX/MapY`。见 §12 疑问 Q1。

---

## 6. 工具集与执行协议

### 6.1 工具清单（名称、参数名、required 均为协议，逐字）

| 工具名 | 参数（`*` = required） | 落点 |
|---|---|---|
| `grid_to_km` | `grid*` | `GridMath.GridToKm(args, turretKm)` |
| `set_assumed_turret_position` | `position*` | 主线程 `SetDeclaredTurret` |
| `fire` | `entityId`, `target`, `bearingDeg`, `distanceKm`, `shell*`, `priority`, `validForSeconds`, `offsetKmX`, `offsetKmY`, `allowDangerouslyFriendlyFire`, `motionFrom`, `motionBearingDeg`, `motionSpeedKmh`, `motionAtTime` | 主线程 `QueueFireMission` |
| `adjust_fire` | `serial*`, `target`, `entityId`, `offsetKmX`, `offsetKmY`, `allowDangerouslyFriendlyFire` | 主线程 `AdjustFireMission` |
| `signal_horn` | （无参数） | 主线程 `PullSignalHorn` |
| `firing_solution` | `target`, `entityId`（二选一，schema 无 required） | 本模块内算 |
| `distance_between` | `a*`, `b*` | 本模块内算（快照） |
| `entities_near` | `center*`, `radiusKm` | 本模块内算（快照） |
| `calc` | `expression*` | `Calculator.Evaluate` |
| `get_assumed_turret_position` | （无参数） | 本模块内算（实时读） |
| `cancel_pending_task` | `serial*` | 主线程 `CancelPendingFcsTask` |
| `requisition_card` | `cardId*`, `bearingDeg`, `startGrid`, `distanceKm`, `priority` | 主线程 `RequestCard` |
| `solve_target` | `lines[]{from*, bearingDeg*, distanceKm}`, `circles[]{from*, distanceKm*}`, `near` | `GridMath.SolveTarget` + 作图 |

**兼容别名（必须保留，防幻觉失手）**：
- `set_turret_position` ≡ `set_assumed_turret_position`
- `get_turret_position` ≡ `get_assumed_turret_position`
- `cancel_pending_task` / `adjust_fire` 的 `serial` 参数接受旧名 `targetId`
- 任何**未知工具名**，若其参数对象含数组字段 `actions` → 按批量 fire 处理
  （历史幻觉形状 `{"actions":[...]}`），逐个当作一次 `fire` 执行。
- 其余未知工具 → 返回 `{"error":"unknown tool '{name}'"}`。

### 6.2 每次工具调用的统一后处理（顺序不可变）
1. 执行得到 `result` 字符串。
2. **时间戳前缀**：若 `EventLog.GameClock` 非空 → `result = $"[@{GameClock}] {result}"`。
   语义：工具回执在**执行时刻**为真，不是在阅读时刻为真。
3. 记录最近调用条目：`$"{name}({args截断}) → {result}"`，
   其中 args 原始 JSON 超 **120** 字符时截断为前 120 + `"…"`；
   列表上限 **20** 条（超出从头裁剪）。持 `_gate` 锁。
4. `TransactionLog.Write("tool", entry, new { name, args = 原始JSON文本, result })`。
5. **事件搭载（关键）**：`EventLog.WaitForEvents(_eventCursor, 0)` 非阻塞取工具执行期间的新事件；
   若有 → 推进 `_eventCursor`（主循环因此**不会重发**），并把
   `"\n[随查战场新事件]\n" + 事件行（与 §4.2 同格式，`\n` 连接）` 追加到 `result` 尾部。
   目的：agent 在**同一轮**内就能对误伤预警/弹着/停火命令反应，不必等下一轮。
   副作用：agent 自身动作触发的事件会回声在回执里（视为无害确认）。
6. **注意**：第 3、4 步记录的是**未搭载事件的** result；搭载只影响返回给 LLM 的文本。

### 6.3 各工具的具体行为与错误文案

**`firing_solution`**（实时读炮塔）
- 主线程读 `ReadTurretLocal()`（超时 10 000 ms），换算成 km。
- `entityId` 分支：`FindVisibleEntity(entityId)`（10 000 ms）；
  为 null → `{"error":"entity '{entityId}' not visible on the map"}`。
- 否则 `target` 分支：`GridMath.ParsePoint(target, turret)`；
  失败 → `{"error":"cannot parse target '{target}'"}`。
- 两者皆无 → `{"error":"need target or entityId"}`。
- 成功返回 JSON 字段：`target`（回显 label）、`bearingDeg`（**保留 2 位小数**）、
  `distanceKm`（3 位）、`turretKm:{x,y}`（各 3 位）、`inMapBounds`（bool）。

**方位角公式（全模块统一）**：
`bearing = atan2(dx, dy) * 180 / π`，`dx = 东向差`、`dy = 北向差`，负数 `+360`。
即 **正北 = 0°、顺时针增大**；距离单位 **km**。

**`ResolvePoint`（`distance_between` / `entities_near` 的端点解析，纯快照数学，不上主线程）**
按序尝试：
1. 空/空白 → 无解。
2. 字面量 `"turret"`（OrdinalIgnoreCase）→ 本轮冻结的 `turretKm`，label = `"turret"`。
3. 快照实体：`e.Id == spec || e.RawId == spec`（**区分大小写的精确相等**），
   坐标 = `MapOffset + e.MapX/Y * MapLocalToKm`，label = `e.Id`。
4. `GridMath.ParsePoint(spec, turretKm)`，label = 原始 spec。

**`distance_between`**
- 解析失败 → `{"error":"cannot resolve a='{spec}' (not a visible entityId, 'turret', grid, or 'kmX,kmY')"}`
  （`b` 同理，仅字母不同）。
- 返回：`a:{label,kmX,kmY}`、`b:{label,kmX,kmY}`（坐标 3 位）、
  `distanceKm`（3 位）、`bearingDegAtoB`（**1 位**，a→b 方向）。

**`entities_near`**
- `center` 解析失败 → `{"error":"cannot resolve center='{spec}' (not a visible entityId, 'turret', grid, or 'kmX,kmY')"}`。
- `radiusKm`：仅当是数字时取用并 `Clamp(0.05, 30.0)`；缺省 **1.0**。
- 命中条件：`dist <= radius` 且 `e.Id != center.label`（把圆心实体自身排除）。
- 按距离升序，**最多 30 条**。
- 每条字段：`id`、`role`、`isAlive`、`distanceKm`（3 位）、`bearingDeg`（1 位）。
- 返回：`center:{label,kmX,kmY}`、`radiusKm`、`count`、`entities[]`。
- **不过滤已死实体、不过滤敌我**——友军/平民普查依赖它能看到全部。

**`get_assumed_turret_position`**（实时读）
- 主线程 `ReadTurretLocal()`（10 000 ms）→ km。
- 网格换算：`col = (int)kmX ∈ [0,26) ? (char)('A' + (int)kmX) : "#"`；
  `grid = $"{col}{(int)kmY + 1} {(int)(kmX * 10) % 10}:{(int)(kmY * 10) % 10}"`。
- 若 `!GridMath.InMapBounds((kmX,kmY))` → 返回
  `{"unreliable":true,"note":"假定炮塔位置在地图之外, 不可信。用其他信息(统帅部电文的铁巢网格/侦查报告反定位)重新set_assumed_turret_position。"}`
  （**不返回坐标**，防止 agent 拿越界值当依据）。
- 否则 `{kmX, kmY, grid}`，坐标 3 位。

**`set_assumed_turret_position`**
- `position` 缺失按空串处理；`GridMath.ParsePoint(pos, turretKm)` 失败 →
  `{"error":"cannot parse position '{pos}' (grid like 'H2 3:4' or 'kmX,kmY')"}`。
- 主线程 `SetDeclaredTurret((float)km.x, (float)km.y)`（15 000 ms）。
- `AppendLog($"turret declared at km({x:F2},{y:F2})", "turret", new { x, y })`。
- 返回 `{"result": <字符串>}`。

**`cancel_pending_task`**
- 取 `serial`（number）；缺失则退回 `targetId`（number）；都没有 →
  `{"error":"serial required (任务唯一编号#N)"}`。
- 主线程 `CancelPendingFcsTask(serial)`（15 000 ms）。
- `AppendLog($"cancel #{serial} -> {result}", "cancel", new { serial, result })`。
- 返回 `{"result": ...}`。

**`adjust_fire`**
- serial 解析同上（含 `targetId` 别名与同一错误文案）。
- 构造 `AdjustFireRequest`：`Serial`、`EntityId`（来自 `entityId`）、
  `TargetPoint`（来自 **`target`**）、`OffsetKmX/Y`（仅数字时取）、
  `AllowDangerouslyFriendlyFire`（**仅当 JSON 值恰为 `true` 才为真**；字符串 `"true"` 不算）。
- 主线程 `AdjustFireMission(req)`（15 000 ms）。
- `AppendLog($"adjust #{Serial} -> {result}", "adjust", new { req, result })`。
- 返回 `{"result": ...}`。

**`requisition_card`**
- `cardId` 缺失或空 → `{"error":"cardId required"}`。
- `bearingDeg`、`distanceKm`：仅数字时取（float?）。
- `priority`：数字时 `Clamp(0,100)`，否则 **50**。
- `startGrid`：字符串，可空。
- 主线程 `RequestCard(cardId, bearingDeg, priority, startGrid, distanceKm)`（15 000 ms）。
- 返回 `{"result": ...}`。**本模块不为其写 AppendLog**（由 mod 侧记 `requisition` 事务）。

**`signal_horn`**
- 主线程 `PullSignalHorn()`（10 000 ms），返回 `{"result": ...}`。

**`calc`**
- `expression` 存在且非空 → 返回 `Calculator.Evaluate(expr)` 的**原始字符串**；
- 否则返回**裸字符串** `need expression`。
- ⚠ 这是唯一不返回 JSON 的工具（见 §12 疑问 Q7）。

**`solve_target`**
- `GridMath.SolveTarget(args, turretKm, out geometry)` 得 JSON。
- 若 `geometry.Solution != null` → `MainThread.Post(() => PlotGeometry(geometry))`
  **异步投递，绝不阻塞 agent**（纯装饰）。

**`PlotGeometry`（在指挥桌上物理作图）**
- 每条观测线：`GameState.MapDrawer.Draw(0, "MapMarkerYellow", from, to)`
- 每个距离圆：`GameState.MapDrawer.Draw(0, "MapMarkerDiscCompass", center, (center.x + radius, center.y))`
  （圆规语义：origin = 圆心，target = 半径端点）
- 解点：`GameState.MapDrawer.Draw(0, "MapMarkerRED", s, s)`（零长度笔画 = 点）
- 首参恒为 **0**；坐标为 km 帧的 `UnityEngine.Vector2`（double → float 强转）。
- prefab 名逐字：`MapMarkerYellow`、`MapMarkerDiscCompass`、`MapMarkerRED`。

**`fire`**
- 构造 `FireMissionRequest`，字段映射：
  `entityId→EntityId`、`target→TargetPoint`、`bearingDeg→BearingDeg`、`distanceKm→DistanceKm`、
  `shell→Shell`（**缺失或 null 时默认 `"HE"`**）、
  `priority→Priority`（数字时 `Clamp(0,100)`，否则 **50**）、
  `offsetKmX/Y→OffsetKmX/Y`、
  `allowDangerouslyFriendlyFire→AllowDangerouslyFriendlyFire`（仅 JSON `true`）、
  `motionFrom→MotionFrom`、`motionBearingDeg→MotionBearingDeg`、
  `motionSpeedKmh→MotionSpeedKmh`、`motionAtTime→MotionAtTime`、
  `validForSeconds→ValidForSeconds`。
  **`MarkerId` 从不由 agent 设置**（DTO 默认 4，标记路径已退役）。
- `label = EntityId ?? TargetPoint ?? $"{BearingDeg:F1}°/{DistanceKm:F2}km"`。
- `stamp = DateTime.Now.ToString("HH:mm:ss")`（在调用前取）。
- **`_firesThisRound++` 发生在主线程调用之前**——即"本轮是否开过火"统计的是**尝试次数**，
  被拒绝/失败的 fire 同样计数（因此不会触发"无行动"记录）。
- 主线程 `QueueFireMission(req)`（15 000 ms）。
- `AppendLog($"fire {label} ({Shell}, P{Priority}) -> {result}", "fire", new { req, result })`。
- 向 `_history` 追加 `$"[{stamp}] fire {label} {Shell} -> {result}"`（持锁）。
- 返回 `{"result": ...}`。
- 语义：**每个目标一次调用**，一轮内可连续多次；fire 立即返回排队结果，实际执行由 FCS 异步完成。

**批量 fire 兜底**：遍历 `actions` 数组逐个执行 `fire`，返回
`{"results":[ "<内层JSON字符串>", ... ]}`（内层结果作为**字符串**嵌套，不是对象）。

---

## 7. 日志与事务

`AppendLog(text, type = "agent", data = null)`：
- 持 `_gate` 锁向 `_log` 追加 `$"[{DateTime.Now:HH:mm:ss}] {text}"`；
- **上限 300 条**，超出从头裁剪至 300；
- 随后 `TransactionLog.Write(type, text, data)`（在锁外）。

本模块产生的事务 `type` 取值（逐字）：
`agent`（默认）、`decision`、`tool`、`fire`、`cancel`、`adjust`、`turret`、`compact`。

`LogSnapshot()` / `RecentToolCalls()` 必须返回**拷贝**（持锁 `ToList()`），
UI 线程绝不能枚举活列表。

---

## 8. 自动压缩与接班简报 `CompactConversation(ct)`

触发条件（每轮决策前，追加 user 消息之前判定）：
`UsageMeter.LastPromptTokens > 400_000 && _messages.Count > 3`。

步骤：
1. `AppendLog($"auto-compact: context {UsageMeter.LastPromptTokens:N0} tokens > {CompactAtPromptTokens:N0}", "compact")`。
2. `Status = "compacting..."`。
3. 向 `_messages` 追加一条 user 消息，内容逐字为（源码里由两段字符串拼接，最终是一整行）：
   > `请把到目前为止的战况压缩成一份接班简报, 只输出简报文本: 1)已确认摧毁的目标 2)已下达但未确认结果的任务 3)存活/待处理目标与其弹种方案 4)观测员/参考点网格等长期情报 5)已学到的弹药与精度教训 6)统帅部的有效指令与限制`
4. `LlmClient.ChatStream(_messages, null, null, _ => { }, ct)` —— **不带工具、不流式回显**。
5. **清空 `_messages`**，只重新放入 `{ role:"system", content: SystemPrompt }`。
6. `_carrySummary = summary`（**不放进 `_messages`**）。
7. `TransactionLog.Write("compact", "conversation compacted", new { summary })`。

代价与后果（必须保留的设计说明）：多付一次总结调用，且下一轮必定缓存未命中；
简报以 `## 前情简报(此前对话已压缩)` 段落注入**下一轮**的 user 消息，且**只注入一次**。

---

## 9. 待命条令（standby）

由 SystemPrompt 承载（逐字保留块内），代码侧的配套约束只有三条，重实现必须保留：
1. 空转退避（§3.2）让"无战术局面"的关卡不会每分钟烧一轮。
2. 合成 `recheck` 事件的文案要求 agent"重新评估"，而非强制行动。
3. `_firesThisRound == 0` 的轮次会在 `_history` 里留一条 `无行动: {前120字}`。
   （该列表当前无读者，见 §12 疑问 Q2。）

---

## 10. 跨模块契约

### 10.1 本模块依赖（须由其他模块提供，签名逐字）

**`AgentBridgeMod`（构造函数注入的宿主实例）**
- `static volatile bool GameFocused`
- `static volatile bool CinematicActive`
- `StateSnapshotDto BuildSnapshot()`
- `UnityEngine.Vector3 ReadTurretLocal()`
- `MapEntityDto? FindVisibleEntity(string entityId)`
- `string QueueFireMission(FireMissionRequest req)`
- `string AdjustFireMission(AdjustFireRequest req)`
- `string CancelPendingFcsTask(int serial)`
- `string SetDeclaredTurret(float kmX, float kmY)`
- `string RequestCard(string cardId, float? bearingDeg, int priority = 50, string? startGrid = null, float? distanceKm = null)`
- `string PullSignalHorn()`
> 所有这些返回的都是**给 LLM 看的中文/英文结果串**，本模块只做 `{"result": ...}` 包装，
> 不解析、不判断成败。

**`MainThread`**
- `Task<T> Run<T>(Func<T> func, int timeoutMs = 10_000)`（本模块一律 `.GetAwaiter().GetResult()` 同步等待）
- `void Post(Action action)`（fire-and-forget，仅用于作图）

**`EventLog`**
- `List<BridgeEvent> WaitForEvents(long since, int timeoutMs)`（`timeoutMs = 0` 表示非阻塞取）
- `volatile string GameClock`（`"HH:mm"`，24 小时制，可能为空串）
- `BridgeEvent { long Seq; string Type; string Source; string Text; string GameTime; object? Data; }`

**`LlmClient`**
- `string ChatStream(List<object> messages, string? toolsJson, Func<string, JsonElement, string>? toolExecutor, Action<string> onDelta, CancellationToken ct)`
- 契约：就地向 `messages` 追加 assistant / tool_calls / tool 轮；
  单次决策的工具轮上限 `MaxToolRounds = 64`；HTTP 超时 300 s；
  每个工具执行前先 `ct.ThrowIfCancellationRequested()`（停止/重置期间不得执行陈旧世界观的工具）。

**`UsageMeter`** — `static long LastPromptTokens`（上一轮的 prompt token 数）。

**`TransactionLog`** — `static void Write(string type, string text, object? data = null)`。

**`GridMath`**
- `(double x, double y)? ParsePoint(string from, (double x, double y) turretKm)`
- `string GridToKm(JsonElement args, (double x, double y) turretKm)`
- `string SolveTarget(JsonElement args, (double x, double y) turretKm, out SolveGeometry geometry)`
- `bool InMapBounds((double x, double y) p)`
- `class SolveGeometry { Lines; Circles; Solution; }`（`Solution` 可空）

**`Calculator`** — `static string Evaluate(string expr)`（三角一律角度制）。

**`GameState.MapDrawer`** — `static void Draw(int id, string prefabName, Vector2 origin, Vector2 target)`。

**`AgentConfig`（MelonPreferences 分类 `[AgentBridge]`）** —— 本模块直读两项：
- `LlmControl`（键名 `LlmControl`，bool，默认 false，**每次启动强制置 false**）
- `ApiKey`（键名 `ApiKey`，string）
  同分类下另有 `BaseUrl`（默认 `https://api.deepseek.com`）、`Model`（默认 `deepseek-v4-flash`）、
  `MaxTokens`（默认 `393216`）、`AutoStart`（默认 true）、`EnableHttpApi`（默认 false）、
  `PriceInputCacheMissPer1M`、`PriceInputCacheHitPer1M`、`PriceOutputPer1M`、`PriceCurrency` —
  由 LlmClient/UsageMeter 使用，不属本模块。

**DTO**（`Dtos.cs`）：`StateSnapshotDto`、`MapEntityDto`、`GunDto`、`FcsStatusDto`、
`CardDto`、`ShellSpecDto`、`ChargeRangeDto`、`FireMissionRequest`、`AdjustFireRequest`、`BridgeEvent`。

### 10.2 本模块对外暴露

- `FdoAgent(AgentBridgeMod mod)`
- `void Start()` / `void Stop()` / `void ClearLog()`
- `bool IsRunning` / `AgentState State` / `string Status` / `string LastReason`
- `string StreamingText` / `bool IsStreaming`
- `IReadOnlyList<string> LogSnapshot()` / `List<string> RecentToolCalls()`
- `enum AgentState { Stopped, Running, Paused, Stopping }`

调用方与时序契约：
- `AgentBridgeMod.ToggleLlmControl()`（F11）：`LlmControl` 翻转后
  `on && !IsRunning → Start()`；`off && IsRunning → Stop()`。
- `AgentBridgeMod.FullReset(reason)`（F9 / 新任务）：`Stop()` → `ClearLog()` → `EventLog.Clear()`
  （**陈旧事件绝不能重放进重启后的新上下文**）。
- 任务阶段轮询：`MissionActive → 其他` 时把 `LlmControl` 置 false 并 `Stop()`；
  `→ MissionActive` 时 `FullReset("new mission — clearing previous conversation")`。
- `OnDeinitializeMelon()` 调 `Stop()`。
- `Ui/AgentWindow` 每帧读 `IsStreaming/StreamingText/LastReason/RecentToolCalls()/LogSnapshot()`。

### 10.3 与 HTTP 调试端点的镜像关系
本模块**不发 HTTP 请求**，但其工具与 `127.0.0.1:17171` 的端点一一对应，
两条路径必须落到同一批 `AgentBridgeMod` 方法（协议保持一致）：
`POST /fire`（≡ `fire`）、`POST /adjust`（≡ `adjust_fire`，body `{targetId, target|entityId, offsetKmX?, offsetKmY?}`）、
`POST /turret`（≡ `set_assumed_turret_position`）、`POST /requisition`（≡ `requisition_card`）、
`POST /horn`（≡ `signal_horn`）、`POST /command`（body `{"text":"..."}` → `commander_order` 事件，
`source = commander`，经事件循环或同轮搭载送达 agent）。

---

## 11. 不变量与防御性规则

- **I1 — 快照绝不打印炮塔坐标。** agent 对自身位置的认知只能来自电文 + 自主校准；
  一旦系统把坐标喂给它，它就会照抄（已观测到的失效模式）。校准状态由本任务内的
  **动作**（工具调用或检测到的手动拖动）判定，不从坐标推断。
- **I2 — 主线程纪律。** 所有游戏对象访问必须经 `MainThread.Run`（需要结果时，同步 + 超时）
  或 `MainThread.Post`（纯装饰、绝不阻塞 agent）。agent 线程直接碰 Unity/Il2Cpp 对象 = 崩溃。
- **I3 — 超时分级。** 读类 10 000 ms（`ReadTurretLocal` / `FindVisibleEntity` / `PullSignalHorn`）；
  写类与快照 15 000 ms（`BuildSnapshot` / `QueueFireMission` / `AdjustFireMission` /
  `CancelPendingFcsTask` / `SetDeclaredTurret` / `RequestCard`）。
- **I4 — 循环不可被异常杀死。** 循环体全包 try/catch；非取消异常一律降级为 5 s 退避后继续。
- **I5 — `_eventCursor` 单线程独占。** 只在 agent 线程上被两处推进：主循环取事件、
  `ExecuteTool` 出口的搭载。两者互斥于同一线程，不需要锁；但**不得**从 UI/主线程触碰。
- **I6 — 集合锁。** `_log` / `_history` / `_recentToolCalls` 一律在 `_gate` 下读写，对外返回拷贝。
- **I7 — 无锁标量。** `Status` / `State` / `StreamingText` / `IsStreaming` / `LastReason` 跨线程无锁，
  必须是单次赋值的不可变值（`StreamingText` 每次重建整串再赋值，不得暴露 StringBuilder）。
- **I8 — 前缀稳定。** `_messages` 一旦追加不得回改；system 消息永远是第 0 条且内容恒定。
  只有 `CompactConversation` 有权整体重建历史。
- **I9 — 单位与坐标约定。**
  地图 local × `3.8164` = km；km 帧原点偏移 `(10.016, 5.235)`。
  方位角：正北 = 0°，顺时针，度。距离：km。
  网格：字母 A→Z 自西向东，数字 1→10 **自南向北**（数字大 = 北）。
  `ShellDefinition.ImpactRadius` 单位是 **km**（HE=0.25、HCHE=0.55、AP=0.15），显示时 ×1000 转米。
  世界时钟为 24 小时制 `"HH:mm"`，是事件 / 快照 / 工具回执 / 电文时刻引用的**唯一时间轴**。
- **I10 — 编码陷阱。** 本模块源文件含大量中文常量，必须以 **UTF-8 无 BOM** 保存。
  **绝不用 PowerShell 的 `Get-Content` / `-replace` / `Set-Content` 修改**——中文 Windows 会按 GBK
  误读 UTF-8 再回写，全文乱码（曾发生，靠 git checkout 挽回）。
- **I11 — MapIntelTable 的键写游戏显示语言**，用 `OrdinalIgnoreCase` 子串匹配本地化关卡名。
- **I12 — 不得把不可见实体喂给 LLM**（上游 `BuildSnapshot` 保证；本模块的
  `distance_between` / `entities_near` 只消费快照，因此天然满足）。
- **I13 — `allowDangerouslyFriendlyFire` 仅接受 JSON 布尔 `true`**，字符串/数字一律视为 false。
- **I14 — 平民保护不可被任何工具参数或关卡情报解除**（学说在 SystemPrompt 内，
  桥侧硬拒由 fire 路径实现，本模块不得提供绕过通道）。

---

## 12. 待裁决 / 疑似死代码 / 矛盾点

- **Q1（协议矛盾）** SystemPrompt 明写"快照 `markers[]` 里玩家标记的位置可视为人工给出的
  兴趣点/目标提示"，但 `BuildCompactState` **从不输出 markers**（`StateSnapshotDto.Markers`
  被采集却被丢弃）。同理 `Teleprinters`（电文全文）也不进快照——电文只经事件流到达。
  是"该补上 markers 行"还是"该删掉学说里这句"？
- **Q2（死代码）** `_history` 列表被 `ExecuteFire` 和"无行动"路径写入、被 `ClearLog` 清空，
  但**没有任何读者**（未对外暴露，UI 读的是 `_log` / `_recentToolCalls`）。删还是接到 UI 上？
- **Q3（死配置）** `AgentConfig.AutoStart`（默认 true，描述"scene binds 后自动启动"）
  在全仓库**无人读取**——启停完全由 `LlmControl` 驱动。是要实现自动启动，还是删掉这个键？
- **Q4（退避状态未复位）** `_idleRechecks` 只在收到真实事件时归零，
  `Start()` / `Loop()` 都不重置它（`_eventCursor` 和 `idleSlices` 都重置了）。
  于是 Stop→Start 后仍可能带着 8 分钟的复查间隔起步。像 bug，确认是否要在 Start 里归零。
- **Q5（统计口径）** `_firesThisRound++` 在 `QueueFireMission` **之前**执行，
  所以全部 fire 都被桥拒绝（友军拦截 / 出界 / 弹种不可购）的一轮不会被记为"无行动"。
  是要改成只统计成功入队，还是刻意保留"尝试即算行动"？
- **Q6（去重口径）** 批内去重键是 `Type + Text`，不含 `GameTime`/`Seq`。两次真实且时间不同、
  但文本完全相同的事件（如同一句反炮击倒计时播报）会被折叠成一条。可接受还是应加时间维度？
- **Q7（返回格式不一致）** `calc` 是**唯一**返回裸字符串而非 JSON 的工具
  （出错时返回字面量 `need expression`）。有意为之（省 token）还是遗漏？
- **Q8（兼容层去留）** 三处纯粹为容忍 LLM 幻觉而存在的兼容路径：
  `set_turret_position` / `get_turret_position` 旧名、`serial` 的 `targetId` 别名、
  `{"actions":[...]}` 批量 fire 兜底。重实现时保留还是清掉？
  （注意 `POST /adjust` 的 body 至今仍用 `targetId` 字段名，与工具的 `serial` 不一致。）
- **Q9（新旧不对称）** 同一轮内，`grid_to_km` / `solve_target` / `distance_between` /
  `entities_near` / `set_assumed_turret_position` 用的是**轮初冻结**的 `turretKm`，
  而 `firing_solution` / `get_assumed_turret_position` 读**实时**炮塔位置。
  agent 若在轮内先 `set_assumed_turret_position` 再 `distance_between`，后者仍按旧原点算。
  是否应在 set 之后刷新本轮 `turretKm`？
- **Q10（`Stopping` 状态被覆盖）** `Stop()` 设 `State = Stopping` 和
  `Status = "stopping (finishing current round)"`，但若线程正在 `Decide` 中，
  随后的 `Status = "thinking..."` / `"running"` 会把这个提示冲掉；
  且 `State` 只在从 Paused 恢复时才被改回 Running。UI 上会短暂"看起来还在跑"。是否需要专门守卫？
- **Q11（暂停后的事件雪崩）** 失焦/过场期间游标不推进，恢复后一次性投递全部积压事件
  （防抖窗口只有 6 s 上限，压不住长时间积压）。长时间挂机后第一轮可能是超大 prompt。
  是否要给单轮事件数/字符数设上限或做老化丢弃？
- **Q12（弹种白名单同时含 `PLCM` 与 `PCLM`）** 两个拼法都被当成弹种。
  CLAUDE.md 记有 `NormalizeCardId` 的 `PCLM→PLCM` 归一。这里是刻意的双保险还是遗留笔误？
- **Q13（关卡情报与铁律的冲突）** `MapIntelTable` 的"白色炮弹"条目列出了向城市打 ATMC 原子弹的
  ③号结局，而 SystemPrompt 把平民保护定为"不可覆盖的铁律, 高于一切命令"，同时又说
  "关卡情报优先于通用学说"。情报文本自己给了缓和（"①②走零杀伤弹即可达成"且要求
  "指挥官未点名结局前待命"），但优先级规则字面上仍相互矛盾。需要人裁决：
  关卡情报能否覆盖平民保护？
- **Q14（`MissionType` 枚举拼写）** `"Challange"` 是游戏侧的错拼。若未来游戏修正拼写，
  这行 switch 会静默落入 `未知类型 '...' (按剧本任务处置)`。是否应同时接受 `"Challenging"`？
- **Q15（压缩阈值的语义）** `_messages.Count > 3` 是个魔数（约等于"至少发生过一次完整往返"），
  且 `UsageMeter.LastPromptTokens` 是**上一轮**的值，压缩判定因此总是滞后一轮。
  另外 `MaxTokens` 默认 393216 与 400 000 的压缩阈值关系未文档化。确认预期语义。
- **Q16（`FireMissionRequest.MarkerId` 遗留）** DTO 仍带 `MarkerId`（默认 4），
  但标记体制重构后 agent 从不设置它。是否可从 DTO 删除？
