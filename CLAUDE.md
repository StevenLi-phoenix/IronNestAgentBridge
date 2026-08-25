# IronNestAgentBridge — 项目知识库

独立 MelonLoader mod：把 *Iron Nest: Heavy Turret Simulator* 的战场信息与 IronNestFCS Smart
火控暴露为本地 HTTP API + 内置 LLM agent（DeepSeek），让 LLM 担任射击指挥官。
与 FCS 解耦（仅反射对接）；FCS fork 在 `C:\Users\stevenli\Codes\IronNestFCS-Smart`（已加 priority 补丁）。
游戏目录：`D:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator`。

## 构建与部署

- `tools\Build.ps1`：游戏运行中拒绝构建（Mods DLL 被锁）；`-m:10` 限并行。
- 游戏开着时只能 `-p:OutputPath=bin\staging\` 构建暂存，关游戏后拷入 `Mods\`。
- FCS Logic 是热重载的：改 `IronNestFCS.Logic` 后落盘到 `UserData\IronNestFCS\` 即时生效（等价 F9）。
- 配置在 `UserData\MelonPreferences.cfg` `[AgentBridge]`（ApiKey/模型/价格/开关）。

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

**FCS 对接（反射链，F9 后必须重解析）**
- `FcsHostMod`(melon "IronNestFCS Smart") → `_reloader`(私有) → `Current`(公有) → `_fcs`(私有) → `FSC`。
- 排任务正道：移动标记 → `MapTable.GetMarkTarget(id)` → 设 bulletType/priority → `FSC.EnqueueTask`。
- Logic 在可回收 ALC 里，勿持强引用；主线程 only；`FcsRuntimeClock.IsFocused` 门槛。
- 我们的 FCS 补丁：`ArtilleryTask.priority`(0-100)，matcher 在槽位数后、装药保护前比较
  优先级向量；P≥90 跳过凑单窗（反炮击"立即执行"）。

**画图（物理正统）**
- 逐条画用实例方法 `placer.RestoreMarker(MapMarkerSaveData)`（追加语义, 实测验证）。
  **陷阱**: 静态 `RestoreMissionMarkers(list)` 是"清空后整体恢复", 会连玩家手绘一起洗掉。
- 存档坐标 == km 帧（实测标定）。prefab：MapMarkerRED/Yellow/White（笔）、MapMarkerDiscCompass
  （圆规，origin=圆心 target=半径端点）。点 = 零长度笔画（origin==target）。
- 侦察机购买实测通过: 插卡→bearing旋钮SetDialValue→购买钮, 卡价每局不同(读实价)。

**征用台**
- 购买 = 纯物理模拟：卡片瞬移到槽位 `(6.4814,-2.4675,-22.0968)` → `DraggableItem.MoveToSlot()`
  → 左右炮拨盘 → 点 "Universal Button"。
- 卡片元数据：`PunchcardRuntime.CurrentDefinition` (`PunchcardDefinitionV2`: ID/Cost/RemainingUses/
  IsRecon/Prefab_ConsoleControls)。侦察机卡 ID：`ScoutPlane` / `ScoutPlane_OnTimeUse`（68 点）。
- 侦察卡插入后生成 `ConsoleControl_CoordinatesBearing(Clone)`，内含 `DialOdometerPunchcardBridge`
  （bearingDial 可 `SetDialValue`；**距离玩家不可选**，起始位置是网格翻牌拨盘）。
- 并发：反射取 `FSC.SharedResources.Requisition`（CoroutineLock），`yield return Acquire()` /
  finally `Release()`，与 FCS 自动购弹互斥。

**UI 陷阱**
- 本游戏 IL2CPP 把 **GUILayout 全家**裁剪了（`GUILayout.Window`/`BeginArea` 全炸
  "Method unstripping failed"）。HUD 只能 `GUI.Box` + `GUI.Label` 手排坐标（FCS 同款）。
  `GUI.Button` 运行时探测，失败自动禁用回退热键。
- 热键：F10 面板、F11 LLM 总控、F12 优先队列、F9 全重置（与 FCS 重置同键联动）。

## Agent 设计

- 决策循环在 mod 内后台线程（`FdoAgent`）；游戏访问经 `MainThread.Run`（逻辑必需，同步）或
  `MainThread.Post`（装饰性如画图，fire-and-forget 绝不阻塞 agent）。失焦自动暂停（不烧 token）。
- LLM 工具：`solve_target`（线/圆交汇精确解算+自动作图）、`grid_to_km`、`requisition_card`
  （bearing only）。**严禁 LLM 手算三角**——手算漂移曾导致 ~0.4km 系统性脱靶。
- 弹种学说：armour=0→HE；armour≥1 单体→AP；APHE=集群杀伤；工事/地下(Fortification/
  supplycash/hostilebunker)→AP；盲射=效力侦察一律 STAR(2点 vs HE/AP 18点)；
  弹种/价格以每局征用台实报清单为准。
- 队列纪律：上炮执行约 1min，但排队可等 15min+；"已下达"≠"已打完"；补射需未击穿报告
  且队列无该目标。
- DeepSeek：`deepseek-v4-flash`，max_tokens 上限 393216；峰谷按北京时间 00:30–08:30 半价；
  持久多轮对话保前缀缓存（命中 90%+），100k prompt tokens 触发 auto-compact 接班简报。
- Transaction log：`UserData\IronNestAgentBridge\transactions-*.jsonl`（决策/工具/用量/征用）。

## HTTP 调试端点（127.0.0.1:17171）

`GET /state` `GET /events` `GET /markers` `GET /console` `POST /fire` `POST /print`
`POST /draw` `POST /draw/clear` `POST /scoutplane`(prefab spawn 备胎)
