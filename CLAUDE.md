# IronNestAgentBridge — 项目知识库

本文件是**逆向工程真值表 + 陷阱清单**——实测得来、读代码读不出来、踩过就不该再踩的东西。

**行为规范的权威不在这里**:总规格见 `REQUIREMENTS.md`(§1 总原则、§2 目标架构、§3 全部裁决、
§4 跨仓契约);九个模块的逐条行为需求见 `docs/reqs/*.md`(mod-core / agent-loop / http-api /
fcs-gw / gamestate / llm-plumb / math / infra-ui / knowledge)。「为什么这么写」在代码的
`///` 注释里。三者冲突时:**REQUIREMENTS.md §3 裁决 > docs/reqs 需求节 > 本文件**。

## 项目现状

- **已完结**:完整战役由 agent 实战通关(含四结局终局关),不再计划新功能,仓库作为成品存档。
- **clean-house 重写已落地**(分支 `cleanhouse`):按 REQUIREMENTS.md 重实现——单程序集、
  瘦入口 + 模块化、死代码清除、§3 声明修正全部生效。**不做桥自身的 ALC 热重载拆分**
  (§3.9-4:用户未拍板,项目收档,有意不引入复杂度)。
- 双端部署:桥直接构建进 `Mods\`;FCS Logic 落 `UserData\IronNestFCS\` 热重载。
- cfg 在 `UserData\MelonPreferences.cfg` `[AgentBridge]`:`EnableHttpApi`(默认 false)、
  `LlmControl`(启动强制 false,F11/面板开)、ApiKey/BaseUrl/Model/MaxTokens/MaxToolRounds/价格表。
- FCS fork:`C:\Users\stevenli\Codes\IronNestFCS-Smart`。
  游戏目录:`D:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator`。

独立 MelonLoader mod:把 *Iron Nest: Heavy Turret Simulator* 的战场信息与 IronNestFCS Smart
火控暴露为本地 HTTP API + 内置 LLM agent(DeepSeek),让 LLM 担任射击指挥官。
与 FCS 解耦(仅反射对接,FCS 不在也能读)。

### 代码地图(clean-house 后的落点)

```
AgentBridgeMod.cs            MelonMod 生命周期 + 组件装配 + 帧循环(一切轮询在此发车);
                             世界钟/任务阶段/反炮击/过场/手动校准五个 0.5s 检查各自 try/catch
NullablePolyfill.cs          逐字保留数据块
Core/MainThread.cs           主线程泵(超时工作项作废、FullReset/换场景清空队列)
Core/PollScheduler.cs        多路轮询节律(bind / map / telegraph / misc / fcs / counter-battery)
Core/EventLog.cs             环形事件缓冲 + GameClock + 长轮询唤醒 + LatestSeq/OldestSeq
Snapshot/SnapshotBuilder.cs  StateSnapshotDto —— 唯一状态视图(/state 与 agent 每轮上下文同一对象)
Fire/FireMissionPipeline.cs  目标解析→偏移限幅→越界→射程→安全普查→运动转录→入队
Fire/BlastSurvey.cs          平民/友军**单一共享谓词** + SurveyBlast(排队普查与误伤巡逻共用)
Fire/ShellTracker.cs         出膛甄别 / 在途清单 / 弹着匹配 / 超时销账 / 友军闯入巡逻 / 队列行标注
Http/BridgeServer.cs         15 个端点;Http/Dtos.cs 全部请求与快照 DTO
Fcs/FcsGateway.cs            FCS 反射网关(解析链、入队、改瞄、取消、卡片请求、状态读取)
GameState/                   MapFrame(坐标系单一真源) MapReader MapDrawer ImpactReader
                             AmmoReader GunStateReader TeleprinterReader RequisitionOperator
                             SignalOperator ScoutPlaneOperator SceneFinder Il2CppSafe
agent/FdoAgent.cs            决策主循环 + 工具分发 + 快照文本渲染 + 自动压缩
agent/Doctrine.cs            SystemPrompt / ToolsJson / MapIntelTable 三块逐字数据集中存放
agent/LlmClient.cs           OpenAI 兼容调用 + 工具轮 + 强制收尾;UsageMeter 计量计价
agent/AgentConfig.cs TransactionLog.cs GridMath.cs Calculator.cs
Ui/AgentWindow.cs            IMGUI 面板(GUI.Box/GUI.Label 手排)
```

**已移除、别再找**(§3 [删除] 裁决):`FireMissionRequest.MarkerId`、配置键 `AutoStart`、
`FcsGateway.EnqueueFromMarker`、`MapReader.TryMoveMarker` / `ReturnMarkerHome` / `MarkerIds` /
`_markerHomes`(整套标记搬运 API)、`FdoAgent._history`、`Result`/`CandidateOf` 的 turretKm 形参、
F12 注释。

## 构建与部署

**陷阱:含中文的源文件绝不用 PowerShell 的 Get-Content/-replace/Set-Content 修改**——
中文 Windows 上会以 GBK 误读 UTF-8 再回写,全文乱码(发生过一次,靠 git checkout 救回)。
只用 Claude 的 Edit 工具或其他 UTF-8 安全的编辑方式。含非 ASCII 字面量的 .cs 一律
**UTF-8 with BOM**;`°` 一律 U+00B0。

- `tools\Build.ps1`:游戏运行中拒绝构建(Mods DLL 被锁);`-m:10` 限并行。
- 游戏开着时用 `tools\Build.ps1 -Staging`(= `-p:OutputPath=bin\staging\`)只做编译检查,
  不碰 `Mods\`;关游戏后再拷入。
- 输出路径由 csproj 的 `<GameDir>` 决定,默认写死本机 Steam 路径,换机器改这一处。
- FCS Logic 是热重载的:改 `IronNestFCS.Logic` 后落盘到 `UserData\IronNestFCS\` 即时生效(等价 F9)。
- **陷阱(事故记录):游戏运行时严禁直接构建 IronNestFCS.Logic.csproj**——它默认输出进
  `UserData\IronNestFCS\`,热重载会**当场重置正在工作的 FCS**(队列/校准/任务全丢)。
  游戏在跑时必须 `-p:OutputPath=bin\staging\` 构建验证,待用户确认(关游戏或明说可以重置)
  再把 DLL 拷进 UserData。部署前先 Get-Process 查游戏。
- **陷阱:绝不在游戏运行中手改 cfg**——游戏按内存值整文件重写(任何一次 Save 触发),
  手改必被清。运行中改开关用热键/面板(F11 LLM),其余等关游戏再改文件。

## 逆向工程结论(均实测验证)

**信息系统**
- "最高统帅部"电文 = `Teleprinter.GetTeleprinter(Primary)`;"战场报告" = Secondary。
  全卷文本:`CaptureMissionState().CurrentFullRich`。`SubmitLines()` 可回打电文。
  (`GameState/TeleprinterReader.cs`;增量判定的 `EndsWith` 分支是防御性保留,未观测到该场景。)
- 指挥桌目标 = "Fire Mission Root" 下 `EntityLocation`/`MapEntity`(ID/Role/State/血量/护甲/
  ImmuneShells/Stars)。迷雾判定:`VisualRoot.activeInHierarchy` + `VisibilityGroup.alpha`。
  绝不把 Visible=false 的实体喂给 LLM(开图作弊)。`Stars` 语义未知,原样透传。
- 坐标系(单一真源 `GameState/MapFrame.cs`):"Draggable Surface" local × **3.8164** = km;
  km 帧原点偏移 (10.016, 5.235)。网格 "H5 0:9":kmX=字母序号+子格/10+0.05(格心),
  kmY=(行-1)+子格/10+0.05。方位角 0°=地图北(local +Y)顺时针,单位向量是 (sin, cos)。
  **绝不做两次换算**——这是本项目的经典坐标 bug。
- 玩家标记 = "Draggable Surface" 下 "MapToken_Artillery"(TMP 文本=编号)。
- **炮塔三兄弟辨析**(同名陷阱): `GameObject.Find("TurretLocation")` 抓到的是真锚点(权威物理位置,
  永不该动); `Canvas/MapRoot/TurretLocation`(带 TurretLocationIcon)是静态图标;
  **可拖动的棋子是 `Draggable Surface/Player Turret Piece`**——它是"指挥部认为炮塔在哪"的
  推断真源, FCS 与桥都以它的 localPosition 为射击原点(摆错→打偏, by design)。
  LLM 用 set_assumed_turret_position 挪它; 玩家手拖同样生效(位置稳定 2s 且距上次上报 >0.2km
  再发一次 `turret_position` 事件)。km(10.02,5.24) 是原点哨兵值,声明为炮位一律拒绝。

**FCS 对接(反射链,F9 后必须重解析)**
- `FcsHostMod`(melon "IronNestFCS Smart") → `_reloader`(私有) → `Current`(公有) → `_fcs`(私有) → `FSC`。
  **每次 Resolve 都重读 `_fcs` 并按引用比对**,FSC 换人即重建缓存;任何一级失败清整条缓存。
  反射成员首次失败打一条 MelonLogger.Warning,之后静默(不再零日志)。
- 排任务正道 = **纯坐标入队**:`FcsGateway.EnqueueAimPoint(local, brg, dist, …, out serial)`
  (反射建 task:targetId=0、hasAimPoint/aimLocal,返回 FCS 分配的 serial)。
  `EnqueueByBearing` 同理,`position` 也填真实 km 帧坐标。
  **桥彻底不移动任何地图标记**;整套标记搬运 API 已删。
- Logic 在可回收 ALC 里,勿持强引用;主线程 only;`FcsRuntimeClock.IsFocused` 门槛。
- 我们的 FCS 补丁:`ArtilleryTask.priority`(0-100),matcher 在槽位数后、装药保护前比较
  优先级向量;P≥90 跳过凑单窗(反炮击"立即执行")。
- 移动目标(FCS fork):ArtilleryTask 带线性运动模型 p(t)=origin+vel·(t−t0)(map-local,
  任务时钟秒)。来源:`trackEntityId`(FCS 自采样测速,雾中继续外推,90s 后 trackingLost)
  或桥经反射注入的 LLM 转录模型。排队期每规划轮外推+RefreshSolution;执行期三段重解:
  pre-aim(装填后、摇仰角前,45s 视界)→ pre-fire(预计弹着偏差>50m 才动炮)→
  manual-wait(等扳机每 3s,弹道台优先级 10)。仰角重解走 `TryAnalyticElevation`
  (线性公式),弹道台只剩超射程 fallback。
- `CoroutineLock` 带优先级队列(高优先放行,同级 FIFO):弹道/装填/击发通道按任务
  priority,卡片请求按请求 priority,后台补火药 20,跟瞄重解 10。
  **陷阱:`Acquire` 有多重载**——桥反射拿锁必须 `GetMethod("Acquire", Type.EmptyTypes)`,
  否则歧义抛异常(旧实现这条路径**从未真正拿到过锁**)。且仅在无 FCS 时才走桥自购。
- 24h 世界时钟:`GenericTimerSceneSync`(怀表/挂钟数据源,CurrentTime=当日秒数)。
  桥 `EventLog.GameClock`("HH:mm")与 FCS `MapTable.MissionNow` 同源;电文时刻引用同轴。
  **场景里可能有多个计时器**,取 CurrentTime 最大的那个(其余是道具或后开的倒计时);
  无世界钟的关卡回退任务秒表("mm:ss"),此时**没有绝对时间轴**。

**FCS 侧行为(跨仓真值,桥依赖之)**
- **任务编号 #N 唯一**:`ArtilleryTask.serial` 入队时由 TaskDispatcher 分配,抢占重排保留,
  F9 归零。adjust/cancel(`FSC.AdjustTaskAim` / `CancelPendingTask`)**只认 #N**。
  T 编号(=标记号,回收复用必重复)已从一切对外显示/寻址中删除。
  stock FCS(无 serial 字段)时 DescribeTask 回退旧 T 前缀。
- **T9/T10 是固定炮位标签**:T9=左炮当前任务瞄点、T10=右炮,由 FCS 自动控制
  (`FSC.GunTargetMarkerLoop` 0.5s + `MapTable.SetGunTargetMarker` + `_gunMarkerHomes`);
  **T1-T8 完全归玩家手动**。桥的面板/快照/摘要一律用 **T9(左)/T10(右)** 字样,无 T1/T2。
- 标记回收/任务标注用 `FcsStatusDto.SerialToMarker` 结构化映射(gateway 反射读 serial+targetId),
  不再正则解析显示串。**出膛判定 = 簿记里的 serial 不在 `SerialToMarker.Keys` 里**
  (`ShellTracker.TrackFiredShells`,无任何物理归位操作)。
- **取消现在会进 RecentTasks**(`Failed: cancelled by commander`),所以出膛甄别不会把取消
  误判为出膛;桥的 cancel 路径**同时自行清簿记**(双保险)。注意 cancel 通道的成功词是
  `"cancelled: …"` 而**不是** fire/adjust 的 `"ok"`。
- `RecentOutcomes` 失败前缀是 `Failed: {reason}`(冒号+空格),桥按前缀切分 + TrimStart 取原因。
  失败判定双保险:`progress=="Failed"` **或** failureReason 非空。
- **炮击顺序规划**(TaskDispatcher.PlanEngagementOrder,每轮规划在解算刷新后重排队列本体):
  优先级带硬外序;带内二维序列优化——方位转塔与仰角摇柄并行,单步成本
  max(Δb/4°s, |Δe|/2°s)(与 AlignmentScore 同一 Chebyshev 度量),带内 ≤10 个用 Held-Karp
  精确 DP、超了同度量贪心;仰角未解算用线性模型估计(distance×12/charge)。队列本体即计划序
  → HUD("计划炮击顺序")、agent 快照、匹配器平局裁决自动对齐。
- adjust_fire 最后时刻改瞄:`ArtilleryTask.aimAdjusted` 把三段重解门扩到静态任务,
  pre-fire 对改瞄任务用 0.03km 细阈值而非 50m 显著性门;`FSC.AdjustTaskAim` 清运动模型改静态点,
  炮上任务校验已装装药射程(超出拒绝→cancel 重排)。**FCS 不等待**——不改就按原瞄点发。
- 卡片请求走 `ConsoleCardRequest` DTO 优先级队列(入队即踢的按需排空协程,P100 中途照样插队),
  桥经 `FSC.RequestConsoleCard(...)` 提交,不再自持锁。
- 弹药/卡片 id 归一化怪癖(SMOKE→SMK、PCLM→PLCM、去 Shell)以 FCS 侧 `NormalizeCardId` 为准;
  **桥白名单保留 PLCM+PCLM 双拼**(游戏资产 id 是 PCLM,上游枚举名是 PLCM)。
- 任务时效:fire 的 `validForSeconds`(可选)→ `ArtilleryTask.validForSeconds` +
  `firstEnqueuedAt`(首次入队时间,抢占回队不重置);TaskDispatcher 规划轮内快检 +
  每秒 SweepExpiredTasks 独立扫——**只撤在队列里等待的**,已上炮不受影响;过期走
  Progress.Failed("时效已过…自动撤销")经 RecentOutcomes 以 任务失败(未发射) 事件报 agent。

**画图(物理正统)**
- 逐条画用实例方法 `placer.RestoreMarker(MapMarkerSaveData)`(追加语义, 实测验证)。
  **陷阱**: 静态 `RestoreMissionMarkers(list)` 是"清空后整体恢复", 会连玩家手绘一起洗掉。
- 存档坐标 == km 帧(实测标定)。prefab:MapMarkerRED/Yellow/White(笔)、MapMarkerDiscCompass
  (圆规,origin=圆心 target=半径端点)。点 = 零长度笔画(origin==target)。
  placerIndex 越界直接拒绝(HTTP 400),不静默改用 0 号。
- 侦察机购买实测通过: 插卡→bearing旋钮SetDialValue→购买钮, 卡价每局不同(读实价)。

**弹道模型(52 个日志样本实证, 残差=里程表舍入 ±0.01°)**
- 线性无阻力: **仰角 = 距离km × 12 / 装药数**, 60° 封顶; 最大射程 = 装药×5km。
  与弹种无关(AP/HE 同解)。FCS fork 的 `FirePlanExecutor.TryAnalyticElevation` 即此公式,
  跟瞄重解全走它, 弹道台只剩超射程 fallback。
- 桥侧硬上限 **30km**(C6 六装药),任何来源解析出的 distKm 超过即拒绝,不分路径。

**征用台**
- 购买 = 纯物理模拟:卡片瞬移到槽位 `(6.4814,-2.4675,-22.0968)` → `DraggableItem.MoveToSlot()`
  → 左右炮拨盘 → 点 "Universal Button"。
- 卡片元数据:`PunchcardRuntime.CurrentDefinition` (`PunchcardDefinitionV2`: ID/Cost/RemainingUses/
  IsRecon/Prefab_ConsoleControls)。侦察机卡 ID:`ScoutPlane` / `ScoutPlane_OnTimeUse`(68 点)。
- 侦察卡插入后生成 `ConsoleControl_CoordinatesBearing(Clone)`,内含 `DialOdometerPunchcardBridge`
  (bearingDial 可 `SetDialValue`;**距离玩家不可选**,起始位置是网格翻牌拨盘——
  `DialToSplitFlipDisplayBinder`,父名含 "Location L"/"Location N",SetFlapDialSymbol 驱动)。
- 实测卡 ID:`ScoutPlane`(侦察机,航程约 12 格,bearingDeg+startGrid);
  `LocationReport`(位置报告约 3 点,**必须 startGrid 网格输入**,电文回报炮位=校准依据);
  `MoveZone`(紧急转移约 65 点,无输入,P100,落点不可预知→转移后必须 LocationReport 重校准)——
  反炮兵机制补充:**击毁任一敌方 FDC 可暂时暂停反炮击倒计时**(敌炮群失指挥;只是暂停不是重置,
  恢复指挥后继续走),最便宜的争时手段,学说里排在摧毁敌炮/MoveZone 之前;**摧毁敌炮本身:
  任务模式=倒计时彻底停止(根治),无尽模式=每毁一门延长倒计时**(敌方补炮,只是买时间);
  `Spotter`(前线观测员 FO,1 点,**startGrid 部署格**,报告离部署点最近敌军的情报,电文回传);
  `MoveDirection`(定向移动,10 点,**bearingDeg+distanceKm**:令铁巢向指定方向移动设定距离,
  常规再部署用——**不会暂停/重置反炮兵倒计时,不是逃生手段**(实测确认);新炮位=旧炮位+
  方向×距离可推算→直接 set_assumed_turret_position,免 LocationReport)。新弹种(卡面实测):`LE`(8 点,中等装药小威力,爆半径 150m,精确定位的
  单个小目标省钱选);`DRIL`(3 点,**训练弹**:混凝土填充无爆炸物,极小有效半径——校射专用,
  无杀伤不揭雾);`PHGN`(光气 10 点,damage=1 半径 **620m**,**仅对"被压制状态"的人员**造成杀伤(实测确认)
  ——未压制步兵/工事/装甲全免疫,单独使用基本无效,只作压制后收尾的组合技,学说默认不选);`TEAR`(催泪 8 点,**damage=0** 半径
  750m,**破隐弹**(实测确认):使隐蔽/伪装单位显身,**不揭战争迷雾**——揭雾用 STAR/侦察,
  破隐用 TEAR,不可互替);`WP`(白磷 10 点,damage=0 半径 750m——**官方描述确认**
  (resources.assets 本地化表 STR_PUNCHCARD_WP_DESCRIPTION):烟云内单位**逃离**,**被压制者
  直接死亡**,有几率引燃火灾——即区域驱逐 + 压制收尾双用途,会驱散目标所以想原地歼灭必须先压制;
  能杀被压制友军+纵火,**不入 IFF 豁免名单**);`CLMN`(集束弹 17 点,官方描述:触地即散 6 枚 HE 子弹药,
  半径 500m,**对步兵和车辆均有效**——与 PCLM 的区别是即时齐落无 10 秒间隔);`INCN`(燃烧弹
  12 点,半径 250m,落点起火有蔓延几率);`APHE`(15 点,穿甲爆破 伤害2+半径250m,集群杀伤);
  `FLCH`(镖箭弹 20 点,大覆盖,**仅杀露天徒步步兵**——载具/工事/掩体内无效);`PRPG`(传单弹
  7 点,官方描述:**压制**敌军+几率诱降,零杀伤——压制组合技起手:PRPG→PHGN/WP 收割;
  对友军压制效果**待实测**,保守处理,**不入零杀伤豁免名单**);
  **指挥官偏好:单个软目标默认 LE**(精确弹打精确坐标,瞄点存疑才升 HE);`PCLM`(集束弹 15 点,官方描述:
  **降落伞延迟集束,6 枚小型 HE 子弹药,每枚间隔 10 秒交错落地**(全程约 1 分钟)——对静止
  集群目标/区域封锁用,移动目标会走脱;子弹药 HE 级对重甲无效;shellSpecs 里的规格要购买
  装填后才出现)。化学弹半径巨大,友军普查自动按实半径拦截。价格每局浮动,读实价。
  `CYAN`/`EQKE`/`THRM` 保留在 ID 全集但**无学说**,agent 视为「规格未知弹种」谨慎使用。
- **挖官方卡面文本的方法**:`grep -aob "卡ID" resources.assets` 找偏移,按偏移切片解 UTF-8——
  本地化 JSON 表明文嵌在 `Iron Nest Heavy Turret Simulator_Data/resources.assets` 里,
  键形如 `STR_PUNCHCARD_<ID>_DESCRIPTION`(含英/德/中等多语言副本,英文段最完整)。
- distanceKm 输入链(MoveDirection 用):requisition_card.distanceKm → `FSC.RequestConsoleCard`
  7 参重载 → ConsoleCardRequest.DistanceKm → BuyCardById 距离拨盘(`DialOdometerPunchcardBridge
  .distanceDial` 物理优先,Distance 读回验证,`SetDistanceInternal` 兜底——与 bearing 同款三段式)。
  桥自购回退路径(`RequisitionOperator`)没有距离拨盘支持,MoveDirection 走不通。
- FCS 的 `FindCard` 保持"最后一个命中"语义(与 `BuyCardById` 行为一致),别改成"第一个"。
- 征用点余额:`MissionStatsTracker.Instance.requisitionPoints`(Int32,游戏侧 ProtectedInt 防篡改)
  → `AmmoReader.ReadRequisitionPoints()` → 快照 `RequisitionPoints` + 快照文本"征用点余额"行;
  购买完成(requisition 事件)与炮弹出膛(shell_fired 事件)都附 `· 征用点余额 N`
  (`ShellTracker.BalanceSuffix()`)。
- 弹药规格缓存**按关卡失效 + 增量合并**:Unbind/换场景清除,重扫时保留已知条目、只增不减
  (规格要装填后才出现,减法会把已知的抹掉)。
- **陷阱:`ShellDefinition.ImpactRadius` 单位是 km**(HE=0.25、HCHE=0.55、AP=0.15)。
  曾按米处理导致快照显示"爆半径0m"、友军拦截/覆盖名单形同虚设。

**预算三层(有意设计,各司其职)**
1. 引擎层**完全不拦 fire**——有的关卡余额 0 但炮膛已装填(打已装填弹不购买),任何桥侧
   "买得起吗"猜测都会误拦(按指挥官要求拆除,含队列预留)。
2. 硬门只管**特殊卡**:`RequestCard` 拒绝卡价>余额;cost 读不到时放行(宁可放过不可误拒)。
3. LLM 靠 prompt 自律(快照给余额与实价清单)。

**作战模式判别**
- **`MissionGraph.MissionType`**(Tutorial/Campaign/Challange/Chill,后两者=无尽)经快照
  `MissionType` 给 agent,快照文本"作战模式"行带反炮兵含义(无尽=毁炮只延时,剧本=全灭停表)。
  **拼写陷阱:游戏枚举拼作 `Challange`**,匹配时同时接受 `Challange` 与 `Challenge` 前缀。
- 场景名不可用于判别——**所有任务都跑在 `Master Turret Scene`**(实测《幽灵炮台》),
  build 里的 Mission*.unity 场景是残留/别用。`SceneName` 字段保留仅供诊断。
- 任务阶段用 `MissionManager.Instance.CurrentPhase` 轮询:离开 MissionActive → 自动停 agent;
  进入 MissionActive → FullReset 清历史。**FullReset 后只停不启**,F11 是唯一 opt-in。

**UI 陷阱**
- 本游戏 IL2CPP 把 **GUILayout 全家**裁剪了(`GUILayout.Window`/`BeginArea` 全炸
  "Method unstripping failed")。HUD 只能 `GUI.Box` + `GUI.Label` 手排坐标(FCS 同款)。
  `GUI.Button` 运行时探测,失败自动禁用回退热键。
- 面板 80% 屏高;折行按**显示宽**算(CJK 计 2);思考取尾/决策取头(14 行封顶)。
- 热键:F10 面板、F11 LLM 总控、F9 全重置(与 FCS 重置同键联动)。

## Agent 设计

- 决策循环在 mod 内后台线程(`agent/FdoAgent.cs`);游戏访问经 `MainThread.Run`(逻辑必需,同步)
  或 `MainThread.Post`(装饰性如画图,fire-and-forget 绝不阻塞 agent)。失焦/过场自动暂停。
  事件先防抖(安静满 1s,上限 6s)+ 去重(键 = `Type+Text+GameTime`)再决策;单轮事件注入
  上限 60 条,更早的折叠成一行「……另有 N 条更早事件(已省略, 最早 @HH:mm)」。
- LLM 工具(13 个,schema 在 `agent/Doctrine.cs` 的 ToolsJson):`fire`、`adjust_fire`、
  `cancel_pending_task`、`solve_target`(交汇解算+自动作图)、`grid_to_km`、`firing_solution`、
  `distance_between`、`entities_near`、`get/set_assumed_turret_position`、`requisition_card`
  (bearingDeg/startGrid/priority/distanceKm)、`signal_horn`、`calc`。
  **严禁 LLM 手算三角/提前量**——定位交给 solve 工具,移动目标提前量交给 FCS 运动模型。
  `calc` 有意回裸字符串(省 token,不包 JSON)。
- **幻觉兼容层(有意保留,别当死代码删)**:旧工具名别名 `set_turret_position` /
  `get_turret_position`;`adjust` 的 `serial` 主字段 + `targetId` 别名;`targetPoint` 主字段 +
  `target` 别名;actions 批量兜底。少一次废轮就值回票价。
- **平民保护铁律(不可覆盖,高于指挥官直令)**:平民 = 实体 `Id` 或 `RawId` 含 `civil` 或
  `hospital`(大小写不敏感),**不看阵营**——某终局关会把难民标成 `role=Enemy`。
  `allowDangerouslyFriendlyFire` 只买断友军风险,对平民无效。判定收敛在 `Fire/BlastSurvey.cs`
  单一谓词,排队普查与误伤巡逻同一份。
  **关卡情报是情报不是授权**:涉平民/炮击友军的结局条目只作信息保留,agent 拒绝亲自执行。
- 零杀伤豁免名单只有 `SMK` / `STAR` / `TEAR` / `DRIL`。**WP 与 PRPG 不在名单里**
  (WP 能杀被压制者+纵火;PRPG 压制效果待实测,保守处理)。
- 弹种学说:armour=0→HE;armour≥1 单体→AP;APHE=集群杀伤;工事/地下→AP;盲射一律
  STAR;杀伤弹之间按"每点覆盖/伤害"性价比选(HCHE 半径 550m ≈ HE 5 倍覆盖、<2 倍价,
  目标群/合并打击优先);弹种/价格以每局征用台实报清单为准。
- 队列纪律:唯一权威=快照 pendingTasks+L/R+**在途炮弹清单**(三态查完才许重排)。
  上炮执行约 1min,排队可等 15min+。在途匹配 3km / 150s 超时销账。
- 运动模型输入约束:`motionFrom`/`motionTo` **只接受绝对定位**(网格或 kmX/kmY),相对方位距离
  返回「运动点必须用绝对网格或 km 坐标」;`motionAtTime` **仅在世界钟(HH:mm)可用时接受**,
  回退秒表的关卡返回「本关无世界钟, 请改用相对描述或省略 atTime」。
- 入队回 ok 但 serial ≤ 0 = **按失败处理**(「FCS 未返回任务编号(版本不兼容?), 任务状态未知」),
  不发入队事件、不建簿记——版本不兼容时宁可空手也不能建假账。
- 关卡情报库 `Doctrine.MapIntelTable`:**仅当前关卡命中时注入快照**("关卡情报(指挥官提供)"
  行;关卡名读 `MissionManager.Instance.CurrentMission.MissionName.Get()` → 快照 `MissionName`,
  **中文子串匹配、键用游戏显示语言**——限中文环境)。现有条目:《敌人如潮》——敌全部自北来;
  侦察自动,**严禁买 ScoutPlane**;侦察动作=等无线电报敌情后对北面打 STAR(不部署 Spotter)。
  《白色炮弹》——战役终局关四结局条件。新经验往表里加即可。
- 指挥官直令通道(**陷阱:Windows curl 内联 `-d` 的中文会被转成 GBK 乱码**——中文体必须先写
  UTF-8 文件再 `--data-binary @file`):`POST /command {"text":"..."}` → `commander_order` 事件
  (source=commander),学说权威层级 **指挥官直令 > 统帅部电文 > 战场报告**;agent 事件循环即刻
  唤醒(工具轮中则经随查事件搭车同轮送达)。外部语音方案只需把转写文本 POST 进来。
- 事件游标 `_eventCursor`(agent 线程独占):主循环取事件推进它;**ExecuteTool 出口把
  工具执行期间新到的事件以"[随查战场新事件]"搭在工具结果尾部**并同步推进游标(主循环不会
  重发)——agent 同轮就能对误伤预警/弹着反应;自身动作触发的事件也会回声在结果里(无害确认)。
- 弹着修正:`ImpactReader` 状态键用 marker **实例 id**(不是位置);去重 0.01 local +
  instanceChanged 组合。**雾中死亡不补报**(反作弊铁律,有意)。
- DeepSeek:`deepseek-v4-flash`,`MaxTokens` 默认 393216(=384k max output,顶格,随每轮请求发);
  峰谷按北京时间 00:30–08:30 半价;持久多轮对话保前缀缓存(命中 90%+)。
  **两个数彼此无关**:MaxTokens 是**输出**上限,400k 是**prompt** 阈值——
  `UsageMeter.LastPromptTokens`(滞后一轮)超 400k 且历史 >3 条消息才触发 auto-compact 接班简报。
  强制收尾的 (系统) user 消息**留在历史里**(前缀缓存稳定优先)。
  缓存计价:DeepSeek 私有字段优先,回退 OpenAI 标准 `usage.prompt_tokens_details.cached_tokens`。
- Transaction log:`UserData\IronNestAgentBridge\transactions-yyyyMMdd.jsonl`(决策/工具/用量/征用)。

## HTTP 调试端点(127.0.0.1:17171,默认关闭)

15 条路由。**数据端点回裸对象;动作端点统一 `{"result": …}` 包装**——200 接受、409 业务拒绝、
400 解析/参数错。**带 `Origin` 请求头的请求一律 403**(挡浏览器 CSRF;curl 与进程内 agent 不受
影响);仍仅绑 127.0.0.1,不引入 token。

```
GET  /state          全量快照,带 latestSeq(生成快照那一刻的事件游标)
GET  /events         长轮询,回 {latest, oldest, events};since 缺省 = 当前 latest
                     (**历史刻意不重放**,要历史显式传 since=0;自己的游标 < oldest 说明断档)
GET  /markers        GET /console        GET /find?q=(>=3 字符)
POST /fire           {entityId | targetPoint|target | bearingDeg+distanceKm, shell, priority,
                      validForSeconds, offsetKmX/Y, allowDangerouslyFriendlyFire, motion*}
POST /adjust         {serial(别名 targetId), targetPoint|target|entityId, offsetKmX?, offsetKmY?}
POST /turret         {kmX, kmY} 声明假定炮位(原点哨兵值/出界一律拒绝)
                     ——注意**没有 /cancel 端点**,取消任务只有 cancel_pending_task 工具一条路
POST /requisition    {cardId, bearingDeg?, priority?, startGrid?, distanceKm?}(与工具同能力)
POST /command        {text} 指挥官直令(不进主线程,游戏暂停/失焦也能落)
POST /horn           POST /print {which, lines[]}
POST /draw           {placerIndex, prefabName, ox, oy, tx, ty}(越界 400)   POST /draw/clear
POST /scoutplane     {kmX, kmY, bearingDeg} —— **cheat/debug 后门**,绕过征用点,LLM 工具不可达
```

## 待实测(留白,别当已知)

- 号角关键词是否命中(无匹配时日志打全量交互件清单,据此补关键词)。
- PRPG 对友军的压制效果(故不入零杀伤豁免名单)。
- LocationReport 回报电文格式(绝对网格 vs 相对方位距离);HCHE 合并打击行为。
- Spotter 回报电文格式与"最近处"参考系;MoveDirection 距离拨盘实际步进/上限、移动耗时。
- 本关地图实测范围日志(`[AgentBridge] sheet extent`)是否与目测图幅一致。
