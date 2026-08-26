# 模块：knowledge（知识库 / 工程真值与陷阱）

## 0. 模块定位

本模块不是一段可运行代码，而是**重实现全过程必须遵守的事实约束集合**。它规定：

- 游戏侧的**逆向真值**（反射路径、对象名、单位、数值模型）——重实现者无法从旧代码推断，只能照抄；
- **坐标系与单位约定**——违反则全系统打偏；
- **工程陷阱**——违反则丢数据、乱码、当场炸掉正在运行的战局；
- **不变量与防御性规则**——线程、Il2Cpp、编码；
- **卡片/弹种数据表**与**学说（doctrine）文本**的归属与逐字保留边界。

凡本节写「必须逐字保留」的，重实现时**原样搬运**，不得改写、不得"优化措辞"、不得翻译。

---

## 1. 逐字保留数据块清单（不在本节展开，重实现时原样搬运）

以下为大段自然语言资产。它们是**产品的一部分**（agent 行为由文本定义），改一个字就是改需求。位置以旧仓库 `C:/Users/stevenli/Codes/IronNestAgentBridge` 为准：

| 数据块 | 位置 | 说明 |
|---|---|---|
| **Agent SystemPrompt** | `agent/FdoAgent.cs:13-238`（C# raw string `SystemPrompt`） | FDC 角色设定 + 权威层级 + 弹种学说 + 侦察学说 + 队列纪律 + 在途炮弹三态判据 + 安全条令。整段逐字保留。 |
| **LLM 工具 schema JSON** | `agent/FdoAgent.cs:240-453`（`ToolsJson`） | 工具名/参数名/描述文本；描述文本本身承载学说（如"严禁心算"）。逐字保留（工具集本身归 agent 模块，但文本归本模块管辖）。 |
| **MapIntelTable 关卡情报表** | `agent/FdoAgent.cs:655-680` | 3 条：`白色炮弹`（终局关四结局条件）、`敌人如潮`、`最终收割`。键是**游戏显示语言**的关卡名子串。逐字保留。 |
| **本知识库正文** | `CLAUDE.md` 全文 | 重实现后仍须以 CLAUDE.md 形式留在仓库内（用户明确偏好项目文档 in-repo）。 |

> 注意：`MapIntelTable` 的键在旧实现里是**中文**，因为它按游戏显示语言做子串匹配。重实现不得改成英文键或关卡 ID，除非同时改判定语义（见开放问题）。

---

## 2. 坐标系与单位约定（不变量，违反即全盘打偏）

1. **地图 local → km 换算**：`"Draggable Surface"` 的 `localPosition` × **3.8164** = km。
2. **km 帧原点偏移**：`(10.016, 5.235)`。即 kmX = local.x × 3.8164 + 10.016（Y 同理用 5.235）。
3. **网格记法 `"H5 0:9"` 解析**：
   - `kmX = 字母序号 + 子格X/10 + 0.05`
   - `kmY = (行号 - 1) + 子格Y/10 + 0.05`
   - `+0.05` 是**格心**偏置，必须保留（否则所有网格输入偏半格）。
4. **存档标记坐标 == km 帧**（实测标定）。画图/读图不得再做二次换算。
5. **`ShellDefinition.ImpactRadius` 单位是 km，不是米**。
   - 已知值：`HE=0.25`、`HCHE=0.55`、`AP=0.15`。
   - 陷阱记录：曾按米处理 → 快照显示"爆半径0m"、友军拦截与覆盖名单全部失效。
   - 对外展示时统一 `ImpactRadius * 1000f` 取整为米。
6. **方位单位**：度（`bearingDeg`），距离单位：km（`distanceKm`）。工具/HTTP 字段名一律带单位后缀，不得裸 `distance`。
7. **时间轴**：24h 世界时钟，`"HH:mm"`。所有事件/快照/工具回执统一前缀 **`[@HH:mm]`**。
8. **任务时钟**：运动模型 `p(t) = origin + vel·(t − t0)`，坐标系为 **map-local**，`t` 单位为**任务时钟秒**。

---

## 3. 弹道模型（游戏真值，52 个日志样本实证）

- **线性无阻力**：`仰角(°) = 距离km × 12 / 装药数`
- **封顶 60°**
- **最大射程 = 装药数 × 5 km**
- **与弹种无关**（AP / HE 同解）。
- 该公式即 FCS fork 的 `FirePlanExecutor.TryAnalyticElevation`；跟瞄重解全走解析式，弹道台（迭代求解）只作**超射程 fallback**。
- 规划器估算仰角时用同一线性模型：`distance × 12 / charge`。
- 残差来源仅为里程表舍入 **±0.01°**。

---

## 4. 游戏侧逆向真值（反射的类型/成员/对象名，必须逐字）

### 4.1 信息系统

- 「最高统帅部」电文 = `Teleprinter.GetTeleprinter(Primary)`；「战场报告」= `Teleprinter.GetTeleprinter(Secondary)`。
- 全卷文本：`CaptureMissionState().CurrentFullRich`。
- 回打电文：`SubmitLines()`。
- 指挥桌目标 = `"Fire Mission Root"` 下的 `EntityLocation` / `MapEntity`，可读 ID / Role / State / 血量 / 护甲 / `ImmuneShells`。
- **迷雾判定**：`VisualRoot.activeInHierarchy` **且** `VisibilityGroup.alpha`。
  - **铁律：`Visible=false` 的实体绝不喂给 LLM**（等同开图作弊）。
- 玩家标记 = `"Draggable Surface"` 下的 `"MapToken_Artillery"`，TMP 文本 = 编号。

### 4.2 炮塔三兄弟辨析（同名陷阱，必须完整保留）

同名 `TurretLocation` 存在三个不同对象，语义完全不同：

1. `GameObject.Find("TurretLocation")` → **真锚点**，权威物理位置，**永不该动**。
2. `Canvas/MapRoot/TurretLocation`（带 `TurretLocationIcon`）→ 静态图标，无语义。
3. **`Draggable Surface/Player Turret Piece`** → 可拖动棋子，是「指挥部**认为**炮塔在哪」的**推断真源**。
   - FCS 与桥**都以它的 `localPosition` 为射击原点**。
   - 摆错 → 打偏，**这是 by design，不是 bug，不得"修正"**。
   - LLM 经 `set_turret_position` 挪它；玩家手拖同样生效。

### 4.3 任务与模式

- 关卡名：`MissionManager.Instance.CurrentMission.MissionName.Get()` → 快照字段 `MissionName`。
- 任务阶段轮询：`MissionManager.CurrentPhase`（用于生命周期自动化）。
- 作战模式判别：**`MissionGraph.MissionType`**，枚举值 `Tutorial` / `Campaign` / `Challange` / `Chill`。
  - **`Challange` 是游戏侧的拼写（少一个 e），必须原样匹配，不得纠正为 `Challenge`。**
  - `Challange` 与 `Chill` = **无尽模式**。
  - → 快照字段 `MissionType`；快照文本「作战模式」行须带反炮兵含义说明（无尽 = 毁炮只延时；剧本 = 全灭停表）。
- **场景名不可用于模式判别**：所有任务都跑在 **`Master Turret Scene`**（实测《幽灵炮台》）；build 里的 `Mission*.unity` 是残留，**别用**。`SceneName` 字段**保留仅供诊断**。

### 4.4 世界时钟

- `GenericTimerSceneSync`（怀表 / 挂钟数据源），`CurrentTime` = **当日秒数**。
- 桥侧 `EventLog.GameClock`（格式 `"HH:mm"`）与 FCS 侧 `MapTable.MissionNow` **同源同轴**；电文时刻引用同一时间轴。

### 4.5 征用点余额

- `MissionStatsTracker.Instance.requisitionPoints`（`Int32`；游戏侧用 `ProtectedInt` 防篡改，读取需照顾这一点）。
- 读取器：`AmmoReader.ReadRequisitionPoints()` → 快照字段 `RequisitionPoints` + 快照文本「征用点余额」行。
- **`requisition` 事件与 `shell_fired` 事件都必须附带余额后缀**，格式（BalanceSuffix）：**`· 征用点余额 N`**。

---

## 5. FCS 对接真值（反射链）

- 链路：`FcsHostMod`（MelonMod 名 **`"IronNestFCS Smart"`**）→ `_reloader`（私有）→ `Current`（公有）→ `_fcs`（私有）→ `FSC`。
- **F9 之后必须整条重解析**（对象全换）。
- Logic 装在**可回收 ALC** 里：**绝不持强引用**。
- **主线程 only**。
- 执行门槛：`FcsRuntimeClock.IsFocused`（失焦时 FCS 挂起，agent 必须同步暂停，不烧 token）。
- 排任务"正道"（历史路径，保留认知）：移动标记 → `MapTable.GetMarkTarget(id)` → 设 `bulletType` / `priority` → `FSC.EnqueueTask`。
- **现行入队路径（权威）**：纯坐标 `FcsGateway.EnqueueAimPoint(local, brg, dist, …, out serial)`
  —— 反射构造 task：`targetId=0`、`hasAimPoint` / `aimLocal`，返回 **FCS 分配的 `serial`**。
  `EnqueueFromMarker` 保留但**未使用**（疑似死代码，见开放问题）。
- 桥侧补丁字段：`ArtilleryTask.priority`（**0–100**）；matcher 在**槽位数之后、装药保护之前**比较优先级向量；**P ≥ 90 跳过凑单窗**（反炮击"立即执行"）。
- 其他必须存在的 FCS 成员名：`ArtilleryTask.serial`、`ArtilleryTask.aimAdjusted`、`ArtilleryTask.validForSeconds`、`ArtilleryTask.firstEnqueuedAt`、`ArtilleryTask.trackEntityId`、`FSC.AdjustTaskAim`、`FSC.CancelPendingTask`、`FSC.RequestConsoleCard(...)`、`FSC.EnqueueTask`、`FSC.GunTargetMarkerLoop`、`TaskDispatcher.PlanEngagementOrder`、`TaskDispatcher.SweepExpiredTasks`、`FirePlanExecutor.TryAnalyticElevation`、`MapTable.SetGunTargetMarker`、`MapTable.MissionNow`、`MapTable.GetMarkTarget`、`FcsStatusDto.SerialToMarker`、`CoroutineLock`、`FcsRuntimeClock.IsFocused`。
- **stock FCS 兼容**：无 `serial` 字段时 `DescribeTask` 回退旧 `T` 前缀显示。桥必须在 FCS 缺席时**读取功能照常工作**（仅火力任务下发不可用）。

### 5.1 编号体系（不变量）

- **T 编号（= 地图标记号）会被回收复用，必重复，因此从一切对外显示/寻址中删除。**
- 每个任务获唯一流水号 **`#N`**（`ArtilleryTask.serial`）：入队时由 `TaskDispatcher` 分配；**抢占重排保留**；**F9 归零**。
- `adjust` / `cancel` **只认 `#N`**。
- **`T9` / `T10` 是固定炮位标签**：`T9` = 左炮当前任务瞄点，`T10` = 右炮。出现在 FCS HUD、桥面板与快照。
- **`T1`–`T8` 完全归玩家手动**，桥**彻底不移动任何地图标记**（入队、adjust 都不挪）。
- `T9/T10` 由 FCS 自动控制：`FSC.GunTargetMarkerLoop` 周期 **0.5s**，走 `MapTable.SetGunTargetMarker` + `_gunMarkerHomes`。
- 桥侧簿记以 **serial 为键**（`_deployedTasks`）。
- **出膛判定**：簿记的 serial **不在** `FcsStatusDto.SerialToMarker.Keys` 中（`TrackFiredShells`）；**无任何物理归位操作**。
- 标记回收 / 任务标注一律走 `FcsStatusDto.SerialToMarker` 的**结构化映射**（gateway 反射读 serial + targetId），**禁止正则解析显示串**。
- FCS 全部日志用 **`#{serial}`**，不得再出现 `T{targetId}`；混合文本（如 `"#2 P90 T5"`）视为缺陷。

### 5.2 移动目标与跟瞄（数值常量）

- 运动模型来源二选一：`trackEntityId`（FCS 自采样测速，雾中继续外推，**90s 后 trackingLost**）或桥经反射注入的 **LLM 转录一次函数模型**（`motionFrom`）。
- 排队期：每规划轮外推 + `RefreshSolution`。
- 执行期**三段重解**：
  1. **pre-aim**：装填后、摇仰角前，视界 **45s**；
  2. **pre-fire**：预计弹着偏差 **> 50m** 才动炮；**对 `aimAdjusted` 任务改用 0.03km 细阈值**（不走 50m 显著性门）；
  3. **manual-wait**：等扳机，每 **3s** 一次，弹道台优先级 **10**。
- 仰角重解走 `TryAnalyticElevation`（第 3 节线性公式）。

### 5.3 CoroutineLock 优先级

- 优先级队列：高优先放行，同级 FIFO。
- 弹道 / 装填 / 击发通道按**任务 priority**；卡片请求按**请求 priority**；**后台补火药 = 20**；**跟瞄重解 = 10**。
- **无参 `Acquire()` 必须保留**（桥反射兼容）。

### 5.4 炮击顺序规划（`TaskDispatcher.PlanEngagementOrder`）

- 每轮规划**在解算刷新后重排队列本体**（队列本体即计划序）。
- 优先级带**硬外序**；带内做二维序列优化。
- 并行运动假设：**方位转塔 4°/s、仰角摇柄 2°/s**；单步成本 = **`max(Δb/4, |Δe|/2)`**（Chebyshev 度量，与 `AlignmentScore` 同一度量）。
- 带内 **≤ 10 个**用 **Held-Karp 精确 DP**；超过则用同度量**贪心**。
- 仰角未解算时用线性模型估计（`distance × 12 / charge`）。
- 计划序自动对齐：HUD（**"计划炮击顺序"**）、agent 快照、匹配器平局裁决。
- 日志须带**估计总调炮秒数**。

### 5.5 任务时效

- `fire` 的可选参数 `validForSeconds` → `ArtilleryTask.validForSeconds` + `firstEnqueuedAt`（首次入队时间，**抢占回队不重置**）。
- 双重扫描：规划轮内快检 + **每秒** `SweepExpiredTasks` 独立扫。
- **只撤在队列里等待的任务；已上炮不受影响。**
- 过期走 `Progress.Failed`，文案含 **"时效已过…自动撤销"**，经 `RecentOutcomes` 以 **`任务失败(未发射)`** 事件报给 agent。

---

## 6. 画图（物理正统）

- **逐条画必须用实例方法 `placer.RestoreMarker(MapMarkerSaveData)`**（追加语义，实测验证）。
- **陷阱：静态 `RestoreMissionMarkers(list)` 是"清空后整体恢复"**，会连玩家手绘一起洗掉。**禁止使用。**
- prefab 名：`MapMarkerRED` / `MapMarkerYellow` / `MapMarkerWhite`（笔）、`MapMarkerDiscCompass`（圆规，`origin` = 圆心，`target` = 半径端点）。
- **点 = 零长度笔画**（`origin == target`）。
- 存档坐标 == km 帧（见第 2 节）。
- 画图属**装饰性**操作，走 `MainThread.Post`（fire-and-forget），**绝不阻塞 agent**。

---

## 7. 征用台真值

### 7.1 购买 = 纯物理模拟

1. 卡片瞬移到槽位坐标 **`(6.4814, -2.4675, -22.0968)`**
2. `DraggableItem.MoveToSlot()`
3. 左右炮拨盘
4. 点 **`"Universal Button"`**

- 三条购买流程共用核心（`InsertCard` / `PressBuy`）+ **统一 `NormalizeCardId`**。
  - **已修 bug 必须复现修复**：`BuyCardById` 曾漏做 `PCLM → PLCM` 归一，导致按归一名买不到卡。
- 卡片请求走 FCS 的 `ConsoleCardRequest` DTO **优先级队列**；桥经 `FSC.RequestConsoleCard(...)` 提交，**桥不再自持锁**。
- 排空协程为**入队即踢的按需排空**（不是 1s 轮询循环）；**P100 中途照样插队**。
- 重试边表：`RetrySides`（左右重复块收表；调度器不得有 `attempted` 之类假动作）。

### 7.2 卡片元数据

- `PunchcardRuntime.CurrentDefinition` → `PunchcardDefinitionV2`，字段：`ID` / `Cost` / `RemainingUses` / `IsRecon` / `Prefab_ConsoleControls`。
- 侦察卡插入后生成 **`ConsoleControl_CoordinatesBearing(Clone)`**，内含 **`DialOdometerPunchcardBridge`**：
  - `bearingDial` 可 `SetDialValue`；
  - **距离拨盘玩家不可选**；起始位置是**网格翻牌拨盘** —— `DialToSplitFlipDisplayBinder`，父对象名含 **`"Location L"` / `"Location N"`**，由 `SetFlapDialSymbol` 驱动。
- **distanceKm 输入链（MoveDirection 用）三段式**（与 bearing 同款）：
  `requisition_card.distanceKm` → `RequestConsoleCard`（**7 参重载**）→ `ConsoleCardRequest.DistanceKm` → `BuyCardById` 距离拨盘：
  **`DialOdometerPunchcardBridge.distanceDial` 物理优先 → `Distance` 读回验证 → `SetDistanceInternal` 兜底**。

### 7.3 官方卡面文本挖掘方法（保留为运维手册）

- 明文本地化 JSON 表嵌在 `Iron Nest Heavy Turret Simulator_Data/resources.assets`。
- 方法：`grep -aob "<卡ID>" resources.assets` 找偏移 → 按偏移切片 → 解 UTF-8。
- 键名形如 **`STR_PUNCHCARD_<ID>_DESCRIPTION`**（含英/德/中多语言副本，**英文段最完整**）。

---

## 8. 卡片 / 弹种数据表（游戏真值 + 指挥官学说）

> **价格每局浮动，一律读实价**（`PunchcardDefinitionV2.Cost`）。下表点数为实测参考值。
> **弹种/价格以每局征用台实报清单为准**；清单外弹种购买必败。

### 8.1 已知弹种 ID 全集

`AP` `APHE` `ATMC` `CLMN` `CYAN` `DRIL` `EQKE` `FLCH` `HCHE` `HE` `INCN` `LE` `PCLM` `PHGN` `PRPG` `SMK` `STAR` `TEAR` `THRM` `WP`

- 归一化必须同时接受 **`PLCM`** 拼写并映射到 `PCLM`（游戏侧两种写法都出现过）。
- 判定「是弹种还是特殊卡」用上述集合（大小写不敏感）：命中 = 弹种，未命中 = 特殊卡（仅经 `requisition_card` 使用）。

### 8.2 弹种数据（实测 / 官方卡面）

| ID | 点数 | 爆半径 | 伤害 | 关键机制 |
|---|---|---|---|---|
| `HE` | — | **250m**（`ImpactRadius=0.25`） | — | 通用高爆；armour=0 默认 |
| `HCHE` | — | **550m**（`0.55`） | — | 半径约 HE 的 2.2 倍、**覆盖面积近 5 倍**、单价通常不到 2 倍 → 目标群/合并打击/需要容错半径时**优先 HCHE 而非连发 HE** |
| `AP` | — | **150m**（`0.15`） | — | armour≥1 单体；工事/地下 |
| `APHE` | 15 | 250m | 2 | 穿甲爆破，集群杀伤 |
| `LE` | 8 | **150m** | — | 中等装药小威力。**指挥官偏好：单个软目标默认 LE**（精确弹打精确坐标）；瞄点存疑才升 HE |
| `CLMN` | 17 | 500m | — | 触地即散 **6 枚 HE 子弹药**，**即时齐落无间隔**；对**步兵和车辆均有效** |
| `PCLM` | 15 | — | — | 降落伞延迟集束，**6 枚小型 HE 子弹药，每枚间隔 10 秒**交错落地（全程约 1 分钟）。对**静止**集群/区域封锁用；**移动目标会走脱**；子弹药 HE 级**对重甲无效**。规格要**购买装填后**才出现在 shellSpecs |
| `INCN` | 12 | 250m | — | 落点起火，有蔓延几率 |
| `FLCH` | 20 | 大覆盖 | — | **仅杀露天徒步步兵**；载具/工事/掩体内无效 |
| `PHGN`（光气） | 10 | **620m** | 1 | **仅对"被压制状态"的人员**造成杀伤（实测确认）；未压制步兵/工事/装甲全免疫。**单独使用基本无效**，只作压制后收尾组合技，**学说默认不选** |
| `TEAR`（催泪） | 8 | 750m | **0** | **破隐弹**（实测确认）：使隐蔽/伪装单位显身；**不揭战争迷雾**。揭雾用 `STAR`/侦察，破隐用 `TEAR`，**不可互替** |
| `WP`（白磷） | 10 | 750m | **0** | 官方描述（`STR_PUNCHCARD_WP_DESCRIPTION`）：烟云内单位**逃离**、**被压制者直接死亡**、有几率引燃火灾。区域驱逐 + 压制收尾双用途；**会驱散目标**，想原地歼灭必须先压制。**能杀被压制友军 + 纵火 → 不入 IFF 豁免名单** |
| `PRPG`（传单） | 7 | — | 0 | 官方描述：**压制**敌军 + 几率诱降，零杀伤。压制组合技起手：`PRPG` → `PHGN`/`WP` 收割。**对友军压制效果待实测，不入 IFF 豁免** |
| `DRIL`（训练弹） | 3 | 极小 | 0 | 混凝土填充无爆炸物 —— **校射专用**，无杀伤、不揭雾 |
| `STAR` | — | — | 0 | 照明/揭雾。**盲射一律 STAR** |
| `SMK` | — | — | 0 | 烟幕 |
| `ATMC` | — | — | — | 原子弹（终局关毁灭结局用） |
| `CYAN` / `EQKE` / `THRM` | — | — | — | 存在于弹种 ID 集，规格以每局实报为准 |

- **化学弹半径巨大**，友军普查必须**按实半径**（`ImpactRadius`，km）自动拦截，不得用固定安全距离。
- **零杀伤弹豁免名单（IFF 豁免）：`SMK` / `STAR` / `TEAR` / `DRIL`**。`WP` 与 `PRPG` **明确不在豁免名单内**。

### 8.3 特殊卡（非弹种，仅经 `requisition_card`）

| ID | 点数 | 输入 | 行为 |
|---|---|---|---|
| `ScoutPlane` / `ScoutPlane_OnTimeUse` | 68 | `bearingDeg` + `startGrid` | 侦察机，航程约 12 格 |
| `LocationReport` | ~3 | **必须 `startGrid` 网格输入** | 位置报告；**电文回报炮位 = 校准依据** |
| `MoveZone` | ~65 | 无输入（**P100**） | 紧急转移；**落点不可预知 → 转移后必须 `LocationReport` 重校准** |
| `Spotter` | 1 | **`startGrid` 部署格** | 前线观测员（FO）；报告离部署点最近敌军的情报，电文回传 |
| `MoveDirection` | 10 | **`bearingDeg` + `distanceKm`** | 令铁巢向指定方向移动设定距离；常规再部署用。**不会暂停/重置反炮兵倒计时，不是逃生手段**（实测确认）。新炮位 = 旧炮位 + 方向×距离**可推算** → 直接 `set_assumed_turret_position`，**免 LocationReport** |

### 8.4 反炮兵机制（游戏真值，学说依据）

按"争时性价比"排序，**最便宜的手段排在最前**：

1. **击毁任一敌方 FDC → 暂时暂停反炮击倒计时**（敌炮群失指挥）。**只是暂停，不是重置**；敌方恢复指挥后继续走。学说里**排在"摧毁敌炮"与 `MoveZone` 之前**。
2. **摧毁敌炮本身**：
   - **任务（剧本）模式** → 倒计时**彻底停止**（根治）；
   - **无尽模式**（`Challange` / `Chill`）→ **每毁一门延长倒计时**（敌方会补炮，只是买时间）。
3. `MoveZone`（贵，落点不可预知）。
- `MoveDirection` **不参与**争时（不影响倒计时）。
- `counter_battery` 倒计时事件：**每 20s 一报**。

---

## 9. 学说（doctrine）—— 归属与摘要

学说正文**逐字保留在 SystemPrompt 内**（`agent/FdoAgent.cs:13-238`）。本节只记录学说必须覆盖的**要点清单**，用于校验搬运是否完整：

- **权威层级（不可动摇）：指挥官直令 > 最高统帅部电文 > 战场报告。**
- **严禁 LLM 手算三角 / 提前量**：定位交给 `solve_target`，移动目标提前量交给 FCS 运动模型，角度计算交给 `calc`。
- **弹种选择学说**：`armour=0` → `HE`；`armour≥1` 单体 → `AP`；`APHE` = 集群杀伤；工事/地下 → `AP`；盲射一律 `STAR`；杀伤弹之间按**"每点覆盖/伤害"性价比**选；单个软目标默认 `LE`；群间距超出 HE 半径但在 HCHE 半径内时**换 HCHE 合并**而不是拆成多发 HE。
- **铁律：任何杀伤弹（`LE`/`HE`/`AP`/`APHE`/`HCHE`/`CLMN`/`INCN` 等）严禁用于侦察。**
- **队列纪律**：唯一权威 = 快照 `pendingTasks` + L/R 炮膛 + **在途炮弹清单**（三态查完才许重排）。
  - 上炮执行约 **1 分钟**；队列深时可等 **15 分钟以上** → 队列越深越克制，低优先级目标宁可不排。
  - **已摧毁（`isAlive=false`）的目标绝不排。**
  - **F9/重置后队列清空**，历史里所有"已排"作废，以快照为准重新规划。
- **在途炮弹三态判据**（逐字在 SystemPrompt 内）：在途 → 已被服务，**严禁重复排队**；不在在途清单 + `isAlive=false` → 已解决；不在在途清单 + 仍 alive → 未命中或任务被清，**可以重新排**（不算重复）。
- **平民保护不可覆盖**：按**实体 id** 判定，**无视阵营标注** —— 某终局关会把难民标成敌方。
- **检查火力条令**：误伤 → 停火 → 整改 → 恢复；**不永久趴窝**。
- **终局关待命条令**：指挥官未点名结局前保持待命，选择权属于指挥官。
- **《最终收割》起手式**、**《敌人如潮》严禁买 ScoutPlane** 等按图学说在 `MapIntelTable` 内，**仅当前关卡命中时注入**。

---

## 10. 工程陷阱（trap）—— 全部为事故记录，必须原样规避

### T1 中文源文件编码陷阱（已发生一次，靠 `git checkout` 救回）
**含中文的源文件绝不用 PowerShell 的 `Get-Content` / `-replace` / `Set-Content` 修改。**
中文 Windows 上会以 GBK 误读 UTF-8 再回写 → **全文乱码**。只用 UTF-8 安全的编辑方式（如 Claude 的 Edit 工具）。

### T2 运行中改 cfg 必被清
**绝不在游戏运行中手改 `UserData\MelonPreferences.cfg`。** 游戏按内存值**整文件重写**（任何一次 Save 触发），手改必被清空。
运行中改开关只能用热键/面板（**F11** 管 LLM）；其余等关游戏再改文件。

### T3 运行时构建 FCS Logic 会当场重置在用 FCS（事故记录）
**游戏运行时严禁直接构建 `IronNestFCS.Logic.csproj`** —— 它默认输出进 `UserData\IronNestFCS\`，**热重载会当场重置正在工作的 FCS**（队列/校准/任务全丢，等价 F9）。
游戏在跑时必须 `-p:OutputPath=bin\staging\` 构建**暂存**验证；待用户确认（关游戏，或明说可以重置）再把 DLL 拷进 `UserData`。**部署前先 `Get-Process` 查游戏。**

### T4 桥自身构建被 DLL 锁阻塞
`tools\Build.ps1` **在游戏运行中拒绝构建**（`Mods` DLL 被锁）；用 `-m:10` 限并行。
游戏开着时只能构建到 `bin\staging\`，关游戏后拷入 `Mods\`。

### T5 `RestoreMissionMarkers` 洗掉玩家手绘
见第 6 节。静态整体恢复 API **禁用**。

### T6 `ImpactRadius` 单位是 km 不是米
见第 2 节第 5 条。曾导致"爆半径0m" + 友军拦截形同虚设。

### T7 IL2CPP 裁掉了 GUILayout 全家
`GUILayout.Window` / `GUILayout.BeginArea` 等**全部抛 `"Method unstripping failed"`**。
HUD 只能用 **`GUI.Box` + `GUI.Label` 手排坐标**（与 FCS 同款）。
`GUI.Button` 须**运行时探测**，失败则**自动禁用并回退到热键**。

### T8 Windows curl 内联 `-d` 的中文会被转成 GBK 乱码
指挥官直令等中文请求体**必须先写 UTF-8 文件再 `--data-binary @file`**。此条须写进 README 与端点文档。

### T9 `TurretLocation` 同名三兄弟
见 4.2。抓错对象 = 射击原点错误。

### T10 场景名不能判别作战模式
见 4.3。所有关卡都在 `Master Turret Scene`。

### T11 `PCLM` / `PLCM` 拼写分裂
见 8.1。归一化必须双向覆盖，否则"按归一名买不到卡"。

### T12 `Challange` 拼写
见 4.3。游戏侧枚举拼写错误，必须原样匹配。

### T13 预算门误拦
**`fire` 完全不拦预算**（含队列预留）—— 有的关卡余额为 0 但炮膛已装填（打已装填弹不需要购买），任何桥侧"买得起吗"的猜测都会误拦。
**预算门只剩特殊卡**：`RequestCard` 拒绝**卡价 > 余额**；**`cost` 读不到时放行**。

---

## 11. 不变量与防御性规则（single out）

### I1 主线程铁律
- **所有游戏状态访问必须经主线程泵。**
- `MainThread.Run` = 同步（逻辑必需）；`MainThread.Post` = fire-and-forget（装饰性，如画图），**绝不阻塞 agent**。
- **HTTP 线程绝不直接碰 Il2Cpp。**
- FCS Logic **主线程 only**。

### I2 Il2Cpp 反射防御
- 每一处反射读写都必须包 `try/catch`：Il2Cpp 侧字段可能缺失（stock FCS / 版本差异 / 裁剪）。
- 读不到就**降级**（返回 null / 跳过该字段 / 回退旧显示），**不得抛穿到主循环**。
- 例：`spec.ImpactRadius` 读取失败 → 视为 0 并继续；`serial` 字段缺失 → 回退 `T` 前缀显示。

### I3 ALC 生命周期
FCS Logic 装在**可回收 ALC**：**不持强引用**；**F9 / 场景切换后整条反射链必须重解析**（自动重绑定）。

### I4 迷雾诚实性
**`Visible=false` 的实体绝不进入快照 / 事件 / LLM 上下文。** 这是产品级铁律（反作弊），不是性能优化点。

### I5 精度不外泄
`impact_hint`（黄箭头脱靶提示）**只转述玩家可见的模糊度**，**不得披露精度参数**（真实偏差数值、解算残差等）。

### I6 平民保护不可覆盖
按**实体 id** 判定，无视阵营标注；**无任何参数可以覆盖**（`allowDangerouslyFriendlyFire` 只覆盖友军，不覆盖平民）。

### I7 编号唯一性
`#N` 唯一、单调、抢占重排保留、F9 归零；**T 编号永不用于寻址**。

### I8 桥不动地图标记
桥在任何路径下（入队 / adjust / 出膛回收）**都不移动任何 `MapToken_Artillery`**。

### I9 焦点门槛联动
`FcsRuntimeClock.IsFocused` 为假时：FCS 任务挂起 → **agent 必须同步暂停**（失焦/CG 自动暂停，不烧 token）。

### I10 事件游标独占
`_eventCursor` 由 **agent 线程独占**。主循环取事件推进它；`ExecuteTool` 出口把工具执行期间新到的事件以 **`[随查战场新事件]`** 搭在工具结果尾部并**同步推进游标**（主循环不会重发）。自身动作触发的事件也会回声（无害确认）。

---

## 12. 跨模块契约（本模块暴露 / 依赖）

### 12.1 本模块**暴露**给其他模块的常量与约定

| 契约 | 消费方模块 |
|---|---|
| km 换算常数 `3.8164`、原点偏移 `(10.016, 5.235)`、网格解析 `+0.05` 格心 | geometry / snapshot / draw / tools(`grid_to_km`) |
| 弹道公式 `elev = distKm × 12 / charge`、封顶 `60°`、`maxRange = charge × 5km` | fcs-gateway / tools(`firing_solution`) / 规划器 |
| `ImpactRadius` 单位 = km；`HE=0.25 / HCHE=0.55 / AP=0.15` | safety(友军普查) / snapshot 文本 |
| 弹种 ID 全集 + `PCLM≡PLCM` 归一 | requisition / snapshot 分类 / fire 校验 |
| 零杀伤豁免名单 `SMK/STAR/TEAR/DRIL` | safety(IFF) |
| 反射路径与成员名全表（第 4、5 节） | gamestate / fcs-gateway |
| `MissionType` 枚举字面量（含 `Challange`） | snapshot / 学说注入 |
| `MapIntelTable`（键 = 关卡显示名子串） | agent(快照注入「关卡情报(指挥官提供)」行) |
| SystemPrompt / ToolsJson 文本 | agent |
| 事件类型名：`commander_order`(source=`commander`)、`counter_battery`、`shell_fired`、`requisition`、`impact_hint`、`任务失败(未发射)` | events / agent |
| 时间戳格式 `[@HH:mm]`；余额后缀 `· 征用点余额 N` | events / snapshot / tools 回执 |
| 快照文本固定行名：`征用点余额`、`作战模式`、`关卡情报(指挥官提供)`、`计划炮击顺序`、`在途炮弹(...)`、`征用台可购弹种及单价(...)`、`征用台特殊卡及单价(...)`、`弹药规格(...)`、`可见实体(entityId必须逐字取自此表)` | snapshot |
| 数值常量：pre-aim `45s`、pre-fire `50m` / `0.03km`、manual-wait `3s`、trackingLost `90s`、`GunTargetMarkerLoop 0.5s`、`SweepExpiredTasks` 每 1s、事件防抖 安静 `1s` / 上限 `6s`、`counter_battery` `20s`、在途弹匹配 `3km` / 超时 `150s`、弹着区闯入监视 `5s`、面板 `80%` 屏高、转塔 `4°/s` / 摇柄 `2°/s`、Held-Karp 阈值 `10`、CoroutineLock 补火药 `20` / 跟瞄 `10`、priority `0-100` / `P≥90` 跳凑单窗 / `MoveZone P100` | 各对应模块 |

### 12.2 本模块**依赖**（须由其他模块提供，本模块只规定其名字与语义）

- HTTP 端点（BridgeServer 模块实现），**路径与端口逐字**：
  监听 **`127.0.0.1:17171`**，仅本机，**默认关闭，需配置开启**。
  `GET /state`、`GET /events?since=N&timeoutMs=25000`、`GET /markers`、`GET /console`、`GET /find`、
  `POST /fire`、`POST /adjust`、`POST /command`、`POST /print`、`POST /draw`、`POST /draw/clear`、
  `POST /turret`、`POST /requisition`、`POST /horn`、`POST /scoutplane`（prefab spawn 备胎）。
  - `POST /command` 体：`{"text":"..."}`
  - `POST /adjust` 体：`{targetId, target|entityId, offsetKmX?, offsetKmY?}`
  - `POST /fire` 体：`{"entityId"|"target"|"bearingDeg"+"distanceKm", "shell", "priority", "validForSeconds", ...}`
- MelonPreferences 配置（section **`[AgentBridge]`**，文件 `UserData\MelonPreferences.cfg`）：
  `ApiKey`、`BaseUrl`（默认 `https://api.deepseek.com`）、`Model`（`deepseek-v4-flash`）、`MaxTokens`（`393216`）、`AutoStart`、`EnableHttpApi`、`LlmControl`。
  - 当前实测 cfg：`EnableHttpApi=true`、`LlmControl=false`（**F11 开**）。
  - 无 ApiKey 时状态串固定为：`no ApiKey — set [AgentBridge] ApiKey in UserData\MelonPreferences.cfg`
- LLM 工具名（agent 模块实现）：`fire`、`adjust_fire`、`solve_target`、`grid_to_km`、`firing_solution`、`get_assumed_turret_position`、`set_assumed_turret_position`、`set_turret_position`、`cancel_pending_task`、`requisition_card`（`bearingDeg` / `startGrid` / `distanceKm` / `priority`）、`signal_horn`、`calc`、`distance_between`、`entities_near`。
- 热键：**F9** 全重置（与 FCS 重置同键联动）、**F10** 面板、**F11** LLM 总控。
- 事务日志路径：`UserData\IronNestAgentBridge\transactions-*.jsonl`（决策/工具/用量/征用）。
- 构建产物路径：`<GameDir>\Mods\IronNestAgentBridge.dll`；`GameDir` 属性可 `-p:GameDir=...` 覆盖；构建命令 `dotnet build -c Release`；依赖 MelonLoader ≥ **0.7**（IL2CPP）。
- 游戏目录（默认）：`D:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator`。
- FCS fork 仓库：`C:\Users\stevenli\Codes\IronNestFCS-Smart`（含 priority 补丁）；上游 `IronNestFCS Smart`。
- LLM 供应商真值（DeepSeek）：模型 `deepseek-v4-flash`；`max_tokens` 上限 **393216**（默认即顶格）；上下文 **1M**，max output **384k**；**北京时间 00:30–08:30 半价**；持久多轮对话保**前缀缓存**（命中 90%+）；**400k prompt tokens 触发 auto-compact 接班简报**。

---

## 13. 诊断与实测残留（须在重实现中保留可观测性）

- 启动/换图时打印本关地图实测范围日志：**`[AgentBridge] sheet extent`**。
- 跟瞄日志链前缀：**`[FCS Track]`**，含 `pre-aim analytic` / `pre-fire correction` 两级。
- 号角（`signal_horn`）**无关键词匹配时，日志必须打全量交互件清单**，供事后补关键词。
- FCS 规划日志须带**估计总调炮秒数**。

---

## 14. 待实测清单（原样继承，属于知识库的"已知未知"）

以下为旧仓库遗留的未验证项，重实现后仍需实测填坑，**不得当作已知事实写死**：

1. 号角关键词是否命中（依赖上节全量清单日志）。
2. `LocationReport` 回报电文格式：**绝对网格 vs 相对方位距离**。
3. HCHE 合并打击的实际行为。
4. 跟瞄 `[FCS Track]` 日志链是否如设计闭合。
5. `Spotter` 回报电文格式，以及"最近处"的**参考系**（离部署点还是离铁巢）。
6. `MoveDirection` 距离拨盘的实际步进/上限，以及移动耗时。
7. 本关地图实测范围日志是否与目测图幅一致。
8. `PRPG` 对**友军**的压制效果（决定它是否需要进 IFF 豁免/拦截名单）。
