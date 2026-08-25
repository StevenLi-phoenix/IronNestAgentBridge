# IronNestAgentBridge — 项目知识库

## 当前状态（2026-08-25 会话交接）

- **双端均已部署**（桥 Mods\ 1:56 版；FCS Logic UserData\ 热部署最新）。
- 本轮新增（桥）：任务生命周期自动化（结束自动停 agent / 新任务 FullReset 清历史，
  MissionManager.CurrentPhase 轮询）；`counter_battery` 倒计时事件（20s 一报）；
  24h 世界时钟时间轴（所有事件/快照/工具回执带 [@HH:mm]）；事件防抖 1s + 去重；
  在途炮弹跟踪（shell_fired 事件 + 快照清单，弹着 3km 匹配/150s 超时销账）；
  fire 偏移参数 + 友军误伤拦截（allowDangerouslyFriendlyFire）+ 覆盖名单回执；
  `impact_hint` 黄箭头脱靶提示转述（只报玩家可见模糊度，不披露精度参数）；
  `signal_horn` 工具 + `POST /horn`；面板 80% 屏高；HCHE 性价比学说；
  LocationReport/MoveZone 卡学说；内部暂存优先队列已整体移除（FCS 原生队列唯一）。
- 本轮新增（FCS fork）：移动目标运动模型（trackEntityId 自动跟踪 + LLM motionFrom
  一次函数转录；三段重解 = pre-aim 45s 视界 / pre-fire 显著性门控 50m / manual-wait 3s）；
  线性弹道解析解（TryAnalyticElevation）；CoroutineLock 优先级队列；HUD 任务行运动后缀。
- cfg：`EnableHttpApi=true`、`LlmControl=false`（F11 开）。
- **待实测**：号角关键词是否命中（无匹配时日志打全量交互件清单，据此补关键词）；
  LocationReport 回报电文格式（绝对网格 vs 相对方位距离）；HCHE 合并打击行为；
  跟瞄 [FCS Track] 日志链（pre-aim analytic / pre-fire correction）。
- **未决提议**：桥自身热重载改造（仿 FCS Host/Logic/ALC 拆分，中等规模重构）——用户未拍板。

独立 MelonLoader mod：把 *Iron Nest: Heavy Turret Simulator* 的战场信息与 IronNestFCS Smart
火控暴露为本地 HTTP API + 内置 LLM agent（DeepSeek），让 LLM 担任射击指挥官。
与 FCS 解耦（仅反射对接）；FCS fork 在 `C:\Users\stevenli\Codes\IronNestFCS-Smart`（已加 priority 补丁）。
游戏目录：`D:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator`。

## 构建与部署

**陷阱：含中文的源文件绝不用 PowerShell 的 Get-Content/-replace/Set-Content 修改**——
中文 Windows 上会以 GBK 误读 UTF-8 再回写，全文乱码（发生过一次，靠 git checkout 救回）。
只用 Claude 的 Edit 工具或其他 UTF-8 安全的编辑方式。

- `tools\Build.ps1`：游戏运行中拒绝构建（Mods DLL 被锁）；`-m:10` 限并行。
- 游戏开着时只能 `-p:OutputPath=bin\staging\` 构建暂存，关游戏后拷入 `Mods\`。
- FCS Logic 是热重载的：改 `IronNestFCS.Logic` 后落盘到 `UserData\IronNestFCS\` 即时生效（等价 F9）。
- 配置在 `UserData\MelonPreferences.cfg` `[AgentBridge]`（ApiKey/模型/价格/开关）。
  **陷阱：绝不在游戏运行中手改 cfg**——游戏按内存值整文件重写（任何一次 Save 触发），
  手改必被清。运行中改开关用热键/面板（F11 LLM），其余等关游戏再改文件。

## 逆向工程结论（均实测验证）

**信息系统**
- "最高统帅部"电文 = `Teleprinter.GetTeleprinter(Primary)`；"战场报告" = Secondary。
  全卷文本：`CaptureMissionState().CurrentFullRich`。`SubmitLines()` 可回打电文。
- 指挥桌目标 = "Fire Mission Root" 下 `EntityLocation`/`MapEntity`（ID/Role/State/血量/护甲/
  ImmuneShells）。迷雾判定：`VisualRoot.activeInHierarchy` + `VisibilityGroup.alpha`。
  绝不把 Visible=false 的实体喂给 LLM（开图作弊）。
- 坐标系："Draggable Surface" local × **3.8164** = km；km 帧原点偏移 (10.016, 5.235)。
  网格 "H5 0:9"：kmX=字母序号+子格/10+0.05（格心），kmY=(行-1)+子格/10+0.05。
- 玩家标记 = "Draggable Surface" 下 "MapToken_Artillery"（TMP 文本=编号）。
- **炮塔三兄弟辨析**（同名陷阱）: `GameObject.Find("TurretLocation")` 抓到的是真锚点(权威物理位置,
  永不该动); `Canvas/MapRoot/TurretLocation`(带 TurretLocationIcon)是静态图标;
  **可拖动的棋子是 `Draggable Surface/Player Turret Piece`**——它是"指挥部认为炮塔在哪"的
  推断真源, FCS 与桥都以它的 localPosition 为射击原点(摆错→打偏, by design)。
  LLM 用 set_turret_position 挪它; 玩家手拖同样生效。

**FCS 对接（反射链，F9 后必须重解析）**
- `FcsHostMod`(melon "IronNestFCS Smart") → `_reloader`(私有) → `Current`(公有) → `_fcs`(私有) → `FSC`。
- 排任务正道：移动标记 → `MapTable.GetMarkTarget(id)` → 设 bulletType/priority → `FSC.EnqueueTask`。
- Logic 在可回收 ALC 里，勿持强引用；主线程 only；`FcsRuntimeClock.IsFocused` 门槛。
- 我们的 FCS 补丁：`ArtilleryTask.priority`(0-100)，matcher 在槽位数后、装药保护前比较
  优先级向量；P≥90 跳过凑单窗（反炮击"立即执行"）。
- 移动目标（FCS fork）：ArtilleryTask 带线性运动模型 p(t)=origin+vel·(t−t0)（map-local，
  任务时钟秒）。来源：`trackEntityId`（FCS 自采样测速，雾中继续外推，90s 后 trackingLost）
  或桥经反射注入的 LLM 转录模型。排队期每规划轮外推+RefreshSolution；执行期三段重解：
  pre-aim（装填后、摇仰角前，45s 视界）→ pre-fire（预计弹着偏差>50m 才动炮）→
  manual-wait（等扳机每 3s，弹道台优先级 10）。仰角重解走 `TryAnalyticElevation`
  （线性公式），弹道台只剩超射程 fallback。
- `CoroutineLock` 带优先级队列（高优先放行，同级 FIFO）：弹道/装填/击发通道按任务
  priority，卡片请求按请求 priority，后台补火药 20，跟瞄重解 10。无参 Acquire() 保留
  （桥反射兼容）。
- 24h 世界时钟：`GenericTimerSceneSync`（怀表/挂钟数据源，CurrentTime=当日秒数）。
  桥 `EventLog.GameClock`（"HH:mm"）与 FCS `MapTable.MissionNow` 同源；电文时刻引用同轴。

**画图（物理正统）**
- 逐条画用实例方法 `placer.RestoreMarker(MapMarkerSaveData)`（追加语义, 实测验证）。
  **陷阱**: 静态 `RestoreMissionMarkers(list)` 是"清空后整体恢复", 会连玩家手绘一起洗掉。
- 存档坐标 == km 帧（实测标定）。prefab：MapMarkerRED/Yellow/White（笔）、MapMarkerDiscCompass
  （圆规，origin=圆心 target=半径端点）。点 = 零长度笔画（origin==target）。
- 侦察机购买实测通过: 插卡→bearing旋钮SetDialValue→购买钮, 卡价每局不同(读实价)。

**弹道模型（52 个日志样本实证, 残差=里程表舍入 ±0.01°）**
- 线性无阻力: **仰角 = 距离km × 12 / 装药数**, 60° 封顶; 最大射程 = 装药×5km。
  与弹种无关(AP/HE 同解)。FCS fork 的 `FirePlanExecutor.TryAnalyticElevation` 即此公式,
  跟瞄重解全走它, 弹道台只剩超射程 fallback。

**征用台**
- 购买 = 纯物理模拟：卡片瞬移到槽位 `(6.4814,-2.4675,-22.0968)` → `DraggableItem.MoveToSlot()`
  → 左右炮拨盘 → 点 "Universal Button"。
- 卡片元数据：`PunchcardRuntime.CurrentDefinition` (`PunchcardDefinitionV2`: ID/Cost/RemainingUses/
  IsRecon/Prefab_ConsoleControls)。侦察机卡 ID：`ScoutPlane` / `ScoutPlane_OnTimeUse`（68 点）。
- 侦察卡插入后生成 `ConsoleControl_CoordinatesBearing(Clone)`，内含 `DialOdometerPunchcardBridge`
  （bearingDial 可 `SetDialValue`；**距离玩家不可选**，起始位置是网格翻牌拨盘——
  `DialToSplitFlipDisplayBinder`，父名含 "Location L"/"Location N"，SetFlapDialSymbol 驱动）。
- 实测卡 ID：`ScoutPlane`（侦察机，航程约 12 格，bearingDeg+startGrid）；
  `LocationReport`（位置报告约 3 点，**必须 startGrid 网格输入**，电文回报炮位=校准依据）；
  `MoveZone`（紧急转移约 65 点，无输入，P100）。价格每局浮动，读实价。
- 并发：卡片购买走 FCS 的 ConsoleCardRequest DTO 优先级队列（ConsoleCardRequestLoop 串行
  执行），桥经 `FSC.RequestConsoleCard(...)` 提交，不再自持锁。
- **陷阱：`ShellDefinition.ImpactRadius` 单位是 km**（HE=0.25、HCHE=0.55、AP=0.15）。
  曾按米处理导致快照显示"爆半径0m"、友军拦截/覆盖名单形同虚设。

**UI 陷阱**
- 本游戏 IL2CPP 把 **GUILayout 全家**裁剪了（`GUILayout.Window`/`BeginArea` 全炸
  "Method unstripping failed"）。HUD 只能 `GUI.Box` + `GUI.Label` 手排坐标（FCS 同款）。
  `GUI.Button` 运行时探测，失败自动禁用回退热键。
- 热键：F10 面板、F11 LLM 总控、F9 全重置（与 FCS 重置同键联动）。

## Agent 设计

- 决策循环在 mod 内后台线程（`FdoAgent`）；游戏访问经 `MainThread.Run`（逻辑必需，同步）或
  `MainThread.Post`（装饰性如画图，fire-and-forget 绝不阻塞 agent）。失焦/CG 自动暂停。
  事件先防抖（安静满 1s，上限 6s）+ 去重再决策；任务结束自动停，新任务自动 FullReset。
- LLM 工具：`fire`（offset/运动模型/友军确认参数）、`solve_target`（交汇解算+自动作图）、
  `grid_to_km`、`firing_solution`、`get/set_assumed_turret_position`、`cancel_pending_task`、
  `requisition_card`（bearingDeg/startGrid/priority）、`signal_horn`。
  **严禁 LLM 手算三角/提前量**——定位交给 solve 工具，移动目标提前量交给 FCS 运动模型。
- 弹种学说：armour=0→HE；armour≥1 单体→AP；APHE=集群杀伤；工事/地下→AP；盲射一律
  STAR；杀伤弹之间按"每点覆盖/伤害"性价比选（HCHE 半径 550m ≈ HE 5 倍覆盖、<2 倍价，
  目标群/合并打击优先）；弹种/价格以每局征用台实报清单为准。
- 队列纪律：唯一权威=快照 pendingTasks+L/R+**在途炮弹清单**（三态查完才许重排）；
  上炮执行约 1min，排队可等 15min+。
- DeepSeek：`deepseek-v4-flash`，max_tokens 上限 393216；峰谷按北京时间 00:30–08:30 半价；
  持久多轮对话保前缀缓存（命中 90%+），100k prompt tokens 触发 auto-compact 接班简报。
- Transaction log：`UserData\IronNestAgentBridge\transactions-*.jsonl`（决策/工具/用量/征用）。

## HTTP 调试端点（127.0.0.1:17171）

`GET /state` `GET /events` `GET /markers` `GET /console` `GET /find` `POST /fire`
`POST /print` `POST /draw` `POST /draw/clear` `POST /turret` `POST /requisition`
`POST /horn` `POST /scoutplane`(prefab spawn 备胎)
