# 模块 mod-core（主 mod 类 / 场景与主循环编排）

来源：`AgentBridgeMod.cs`（1013 行，全文通读）。本节描述该模块**必须做到什么**，不描述旧实现的写法；
但 HTTP/事件/配置/日志/反射标识符、单位与数值常量、消息文本原文属于协议本体，按原样逐字记录。

---

## 1. 模块职责

本模块是整个 mod 的唯一 MelonLoader 入口与编排中枢。它必须承担：

1. MelonMod 生命周期（初始化、反初始化、场景加载、每帧更新、OnGUI）。
2. 主线程泵：把后台线程（HTTP 监听线程、agent 决策线程）的游戏访问请求在 Unity 主线程上排空。
3. 各类轮询调度（地图、弹着、电传、FCS 摘要、CG 检测、世界时钟、反炮击倒计时、误伤巡逻）。
4. 快照组装（`StateSnapshotDto`）——agent 与 HTTP `/state` 的唯一状态视图。
5. 事件产出（`EventLog.Append`）——本模块负责 CG、反炮击、出膛/弹着、误伤预警、炮位、征用、信号等类型。
6. 火力任务入队/改瞄/取消的**安全层**：坐标解析、越界拒绝、平民保护、友军误伤拦截、盲射警告、
   射程校验、偏移限幅、运动模型转录。
7. 在途炮弹簿记（`TrackFiredShells` 甄别"已发射" vs "任务失败未发射"、弹着匹配、超时销账）。
8. 全局开关与全重置（F9/F10/F11 热键、任务阶段自动化）。

### 程序集元数据（必须逐字保留）

```
[assembly: MelonInfo(typeof(IronNestAgentBridge.AgentBridgeMod), "IronNest Agent Bridge", "0.1.0", "stevenli")]
[assembly: MelonGame()]
```

`MelonGame()` 无参 = 对所有游戏生效，不得加游戏名限制。

---

## 2. 坐标、单位与数值约定（协议本体，逐字）

| 量 | 值 | 说明 |
|---|---|---|
| 地图 local → km 比例 | `3.8164` | "Draggable Surface" 局部坐标 × 3.8164 = km |
| km 帧原点偏移 X | `10.016` | `kmX = 10.016f + local.x * 3.8164f` |
| km 帧原点偏移 Y | `5.235` | `kmY = 5.235f + local.y * 3.8164f` |
| 反向 | `local = (km - offset) / 3.8164` | |
| 弧度→度 | `57.29578f` | 方位角 `Atan2(dx, dy) * 57.29578f`，注意参数顺序是 **(Δx, Δy)**，即北为 0°、顺时针 |
| 方位角归一 | `(b % 360f + 360f) % 360f` | |
| `ShellDefinition.ImpactRadius` 单位 | **km** | 显示给人时必须 `* 1000f` 转米（`:F0`） |
| 速度换算 | `kmh / 3600f / 3.8164f` = local units/秒 | |
| 运动模型速度分量 | `velX = sin(rad) * v`，`velY = cos(rad) * v` | rad = `bearingDeg * π / 180` |

### 时间/周期常量

| 常量 | 值 | 含义 |
|---|---|---|
| `BindRetrySeconds` | `2f` | 场景未绑定时的重试间隔 |
| `MapPollSeconds` | `0.5f` | 地图 + 弹着 + 超时销账 + 误伤巡逻门 |
| `TelegraphPollSeconds` | `1.0f` | 电传轮询 |
| FCS 摘要周期 | `2f` | 也驱动 `TrackFiredShells` 与卡片结果检查 |
| CG/杂项检查周期 | `0.5f` | CG、手动校准、任务阶段、反炮击、世界时钟 |
| 反炮击播报周期 | `20f` | 运行中每 20 秒一报 |
| 误伤巡逻周期 | `5f` | 独立于 0.5s 地图轮询的二级节流 |
| `InFlightTimeoutSeconds` | `150f` | 在途炮弹绝对超时上限 |
| `ImpactMatchKm` | `3f` | 弹着与在途炮弹的匹配半径（km） |
| FullReset 后重绑延迟 | `1f` | 与 2f 的常规重试不同 |
| 号角按下→松开延迟 | `0.15f` 秒 | `WaitForSeconds` |
| 手动校准位移阈值 | `0.02f`（map local） | X 或 Y 任一超过即判定被拖动 |
| 原点哨兵容差 | `0.15f` km | |
| 偏移上限 | `±0.5f` km | X、Y 各自独立判定 |
| 友军"贴近"环 | `blastKm * 1.5f` | |
| 爆半径有效下限 | `> 0.001f` km | 否则视为无爆炸，跳过普查 |
| 默认最大射程回退 | `40f` km | 弹种规格读不到装药射程表时 |
| 飞行时间估算 | `distKm / 0.4f + 25f` 秒 | 即 0.4 km/s 弹速 + 25 秒固定开销 |
| `InFlightShell.FlightEtaSeconds` 默认值 | `60f` | 记录类型默认值 |

---

## 3. MelonMod 生命周期

### 3.1 `OnInitializeMelon`

必须按此顺序：

1. `AgentConfig.Initialize()`（必须最先，后续一切读配置）。
2. 把 FCS 的征用锁提供者装到 `RequisitionOperator.RequisitionLockProvider`，形式为惰性委托
   `() => _fcs.GetRequisitionLock()`（不得在初始化时求值——FCS 此时可能尚未加载）。
3. 构造 agent（`FdoAgent`，持有对本模块的引用），**但不启动**。
4. 若 `AgentConfig.EnableHttpApi` 为 true：构造 `BridgeServer` 并 `Start()`；启动失败必须捕获异常并
   记录 `[AgentBridge] failed to start HTTP server on port {BridgeServer.Port}: {ex.Message}`（Error 级），
   且**不得**让异常冒泡杀死 mod。
5. 若为 false：记录 `[AgentBridge] HTTP API disabled (EnableHttpApi=false)`（Msg 级）。

### 3.2 `OnDeinitializeMelon`

停止 agent，停止 HTTP 服务器。两者都必须容忍 null。

### 3.3 `OnSceneWasLoaded(buildIndex, sceneName)`

解绑地图、`GridMath.ResetMapBounds()`、电传 reader `Reset()`、把下次绑定尝试推到
`realtimeSinceStartup + BindRetrySeconds`。

### 3.4 `OnGUI`

只有当 **agent 非空 且 地图已绑定 且 `CinematicActive == false`** 三条同时成立时才绘制面板。
这刻意镜像 FCS 的隐式行为（场景未绑定不出 HUD），并额外加了摄像机切换的 CG 门（覆盖任务中途过场）。

### 3.5 `OnUpdate`（每帧）

固定开头两步，顺序不可换：

1. `GameFocused = UnityEngine.Application.isFocused`
2. `MainThread.Pump()`

随后取一次 `now = UnityEngine.Time.realtimeSinceStartup`，全帧复用（不得多次取样）。之后依次：

- **绑定重试**：`!IsBound && now >= _nextBindAttempt` → 推进 `_nextBindAttempt = now + 2f`，尝试绑定。
  绑定成功后必须设置本关射击包线：
  - 若 reader 报告了 `KmBounds`：`GridMath.SetMapBoundsKm(MinX, MinY, MaxX, MaxY)`，日志
    `[AgentBridge] tactical map bound; sheet extent km({MinX:F1},{MinY:F1})-({MaxX:F1},{MaxY:F1})`
  - 否则：`GridMath.ResetMapBounds()`，日志
    `[AgentBridge] tactical map bound; sheet unmeasured — generous bounds fallback`
- **地图节拍（0.5s，且必须已绑定）**：依次
  1. 地图轮询并发事件；失败记 Warning `[AgentBridge] map poll failed: {ex.Message}`
  2. 弹着轮询，传入地图 surface 与弹着解析回调 `OnShellImpact`；失败**静默吞掉**
  3. `ResolveOverdueShells()`
  4. `PollFriendlyIntrusions(now)`
- **电传节拍（1.0s，不要求绑定）**：轮询并发事件；失败记 Warning `[AgentBridge] telegraph poll failed: {ex.Message}`
- **热键**（整块包 try/catch）：读 `UnityEngine.InputSystem.Keyboard.current`，null 则跳过
  - `f10Key.wasPressedThisFrame` → 切换面板可见性
  - `f11Key.wasPressedThisFrame` → `ToggleLlmControl()`
  - `f9Key.wasPressedThisFrame` → `FullReset("F9")`（与 FCS 的 F9 计划重置同键同语义）
- **0.5s 杂项节拍**：CG 检测 / 手动校准检测 / 任务阶段 / 反炮击 / 世界时钟，**每一项各自独立 try/catch**，
  任一抛异常不得影响其余项。
- **2s FCS 节拍**（整块 try/catch）：
  1. 读 FCS 状态，组装 `LastFcsSummary`（见 §4）
  2. `TrackFiredShells(status)`
  3. 读征用台卡片请求结果；非空且与上次不同 → 记住它，发 `requisition` 事件与事务日志

---

## 4. FCS 摘要文本（面板用，逐字格式）

```
FCS: pending={PendingCount} done={CompletedTaskCount} fail={FailedTaskCount}
```
若 `LeftTask != null`，追加换行 + `T1(左): {LeftTask}`；
若 `RightTask != null`，追加换行 + `T2(右): {RightTask}`。

> 注：此处标签写作 `T1(左)/T2(右)`，而项目其余部分（FCS HUD、快照）约定固定炮位标签是 **T9=左炮、T10=右炮**。见 §21 待澄清。

暴露为 `LastFcsSummary`（只读属性，面板读取）。

---

## 5. CG（过场动画）检测

**判据**：场景绑定时捕获一次"基线游戏相机"（`UnityEngine.Camera.main`）；此后 `Camera.main` 为 null
或不再是同一个对象实例（引用比较，非 `==`）即判定 CG 进行中——过场总会切相机。

- 基线为空时：仅在**地图已绑定且 cam 非空**时捕获基线；此期间强制 `CinematicActive = false`。
- 状态翻转时必须同时产出：
  - 日志 `[AgentBridge] cinematic {started|ended} (main camera: {name 或 "none"})`
  - 事件 `EventLog.Append("cinematic", "game", "cinematic started" | "cinematic ended")`

`CinematicActive` 必须是 **`public static volatile bool`**：agent 后台线程读它来暂停决策，面板读它来隐藏。

---

## 6. 焦点镜像

`GameFocused` 必须是 `public static volatile bool`，初值 `true`，每帧从 `Application.isFocused` 镜像。
agent 后台线程据此暂停（`paused (game unfocused)`）。

---

## 7. 世界时钟

**必须**优先镜像游戏内 24 小时世界钟：反射/查找类型 `GenericTimerSceneSync`，用
`UnityEngine.Object.FindObjectsOfType<GenericTimerSceneSync>()` 枚举，取 `CurrentTime` **最大**的那个作为世界钟
（成员：`TimerID`、`CurrentTime`，`CurrentTime` = 当日秒数）。枚举时对每个候选记日志：

```
[AgentBridge] world clock candidate '{TimerID}' t={CurrentTime:F0}s
```

- 找到且 `CurrentTime > 0f` 时：
  - `MissionClockSeconds = t`
  - `EventLog.GameClock = $"{(int)(t / 3600) % 24:00}:{(int)(t / 60) % 60:00}"`（**"HH:mm"**）
  - 立即 return
- 任一异常 → 把缓存的世界钟对象置空（下次重新搜索），并继续走回退路径。

**回退路径**：`MissionStatsTracker.Instance`（成员 `timerRunning`、`timerValue`）。
`Instance` 为 null 或 `timerRunning == false` 时什么都不做。否则：
- `MissionClockSeconds = timerValue`
- `EventLog.GameClock = $"{(int)(t / 60):00}:{(int)(t % 60):00}"`（**"mm:ss"** 秒表格式）

回退路径同样整段 try/catch 静默。

`MissionClockSeconds` 是 `public static volatile float`，供运动模型时间戳使用。

---

## 8. 任务阶段生命周期自动化

数据源：`MissionManager.Instance.CurrentPhase`（枚举 `MissionManager.GamePhase`，关注值 `MissionActive`）。
`Instance` 为 null 或读取抛异常 → 直接放弃本次。

必须做到：

- 记住上一次采样的相位；相同则无操作。
- **开机后第一次采样不算迁移**（`prev == null` 时只记录不动作），避免启动瞬间误触发。
- **离开 `MissionActive`**（→结算画面/回地图/回菜单）：
  - 日志 `[AgentBridge] mission ended ({prev}->{phase}) — agent auto-stop`
  - 事务日志 `TransactionLog.Write("mission", $"mission ended ({prev}->{phase}); agent auto-stopped")`
  - 若 `AgentConfig.LlmControl` 为 true，置为 false
  - 若 agent 在跑，停止它
- **进入 `MissionActive`**：`FullReset("new mission — clearing previous conversation")`
- **agent 永不自动启动**：F11（或面板按钮）是每局唯一的 opt-in。

---

## 9. 反炮击倒计时中继

数据源：`CounterBatteryTimer.Instance`，成员 `IsRunning`、`IsExpired`、`IsPermanentlyStopped`、`TimeRemaining`。
`Instance` 取值本身要 try/catch；实例为 null 时把"曾在运行"标志清零并返回。四个属性的读取合并在一个
try/catch 里，任一失败则整轮放弃（保持状态不变）。

时间格式化：`$"{(int)(s / 60):00}:{(int)(s % 60):00}"`。

状态机（优先级自上而下）：

1. `IsPermanentlyStopped`：若此前在运行 → 事件 `counter_battery` / `game` /
   `反炮击倒计时已永久解除 — 威胁排除`；清"曾在运行"。
2. `IsExpired`：若此前在运行 → 事件 `反炮击倒计时归零 — 敌炮火正在覆盖本阵地`；清"曾在运行"。
3. `!IsRunning`：清"曾在运行"，无事件。
4. 运行中且此前未运行（**启动沿**）：置"曾在运行"，把下次播报定到 `now + 20f`，事件
   `$"反炮击倒计时启动: 剩余 {Fmt(remaining)} — 归零时敌炮火覆盖本阵地"`
5. 运行中且到播报点：下次播报 `now + 20f`，事件 `$"反炮击倒计时: 剩余 {Fmt(remaining)}"`

---

## 10. 炮位校准状态

`TurretCalibrated` 是**行为标志而非位置属性**：只有本局有人（agent 工具或被检测到的玩家手拖）
**主动放置过棋子**才为真。快照里透出 `TurretCalibrated`。

### 10.1 手动校准检测（0.5s 节拍）

- 地图未绑定 → 直接返回。
- 读当前棋子 map-local 位置；与上次记录比较，`|Δx| > 0.02f || |Δy| > 0.02f` **且** 当前尚未标记为已校准
  → 置 `TurretCalibrated = true`，发事件
  `EventLog.Append("turret_position", "map", "turret piece was moved manually — treated as calibrated")`
- 每次都更新"上次位置"缓存（无论是否触发）。

### 10.2 `SetDeclaredTurret(kmX, kmY)`（agent/HTTP 调用，主线程）

顺序校验：

1. `GridMath.InMapBounds((kmX, kmY))` 为假 →
   返回 `$"km({kmX:F1},{kmY:F1}) is outside the map — rejected (check the grid conversion)"`
2. **原点哨兵拒绝**：`|kmX - 10.016f| < 0.15f && |kmY - 5.235f| < 0.15f` →
   返回 `"km(10.02,5.24) 是地图原点(未校准哨兵值), 不是真实炮位 — rejected。校准依据只能是统帅部电文里的铁巢网格"`
   （理由：这个值就是快照里未放置棋子的占位值，模型把它回抄回来永远不是真实炮位。）
3. 委托地图模块执行落位，拿到结果串。若结果**不包含** `"not"` 且**不包含** `"rejected"`，则视为成功：
   置 `TurretCalibrated = true` 并刷新"上次位置"缓存（防止随后被手动校准检测重复触发）。
4. 无论成败，都发事件 `EventLog.Append("turret_position", "map", result)`，并把 result 原样返回。

---

## 11. 在途炮弹与任务簿记

### 11.1 数据结构

已排队任务簿记：**流水号 `#N` → 任务描述**（label + 弹种 + 弹着 km 坐标 + serial + 预计飞行秒数）。
**不涉及任何物理标记**：地图棋子 T1–T8 归玩家手动，T9/T10 归 FCS 自动控制，桥永不移动标记。

在途炮弹条目字段（记录类型）：`Label`、`Shell`、`KmX`、`KmY`、`FiredAt`（`realtimeSinceStartup`）、
`FiredAtGame`（出膛时的游戏钟字符串，默认 `""`）、`Serial`（默认 `0`）、`FlightEtaSeconds`（默认 `60f`）。

流水号提取正则（**逐字**）：

```csharp
new(@"^#(\d+)\b", RegexOptions.Compiled)
```

### 11.2 `TrackFiredShells(FcsStatusDto status)`（2s 节拍）

判据：**簿记中的 serial 不再出现在 `status.SerialToMarker` 的键集合中**，即该任务离开了 FCS 活动集
（pending + 左右炮位）。**必须使用结构化映射，绝不解析显示字符串。**

对每个消失的 serial：先从簿记移除，然后二选一：

- **失败甄别**：`status.RecentOutcomes` 里该 serial 的结果以 `"Failed"` 开头（`StringComparison.Ordinal`）
  → 这是**未发射**的失败（发射盘故障、弹道拒绝、时效过期等）。原因串 = 去掉前 8 个字符
  （即 `"Failed: "` 之后的部分）；长度不足 8 时用 `"unknown"`。发事件
  `fcs_task_update` / `fcs` /
  ```
  ⚠任务失败(未发射): #{Serial} {Label} ({Shell}) — {why}。目标未被服务; 按失败原因处置(装药/射程问题就改打近目标或换弹, 而不是原样重排)
  ```
  **不得**记为在途炮弹（否则 agent 会一直等一发根本没出膛的炮弹）。
- **否则视为已出膛**：加入在途清单，`FiredAt = realtimeSinceStartup`，`FiredAtGame = EventLog.GameClock`；
  发事件 `shell_fired` / `fcs` /
  ```
  炮弹出膛: #{Serial} {Label} ({Shell}) 已在飞行途中, 等待弹着 — 勿重复排队该目标{BalanceSuffix()}
  ```

簿记为空时整个流程短路。

### 11.3 弹着匹配 `OnShellImpact(kmX, kmY)`

由弹着轮询模块作为回调调用。在在途清单里找**欧氏距离最近且 < `ImpactMatchKm`(3km)** 的一发，
命中则从清单移除并返回身份串 `$"#{Serial} {Label} ({Shell})"`；无命中返回 `null`
（调用方据此决定 `shell_impact` 事件文本里是否点名任务）。

### 11.4 超时销账 `ResolveOverdueShells()`

**必须有**：每发炮弹的实际"逾期阈值" = `min(FlightEtaSeconds, InFlightTimeoutSeconds)`。
超过即判定已落地并销账，**并且必须发事件**（绝不能静默过期，否则 agent 会把它读成"还在飞"）：

事件 `shell_impact` / `map` /
```
弹着推定: #{Serial} {Label} ({Shell}) 已超预计飞行时间, 判定已落地并销账 — 弹着标记未移动通常=与前一发落点几乎重合; 可重新评估该目标
```

设计理由：每门炮只有一个弹着标记，同一位置重复落弹时标记不移动，物理上无法产生第二次弹着信号。

### 11.5 在途清单文本 `DescribeInFlight()`

每条：
```
#{Serial} {Label} ({Shell}, 出膛@{FiredAtGame 或 "?"}, 已飞{now - FiredAt:F0}s/预计{FlightEtaSeconds:F0}s)
```
进入快照 `InFlightShells`。

### 11.6 任务串标注 `AnnotateTask(desc)`

对 FCS 给出的任务显示串，用上述正则取出 `#N`，若簿记里有该 serial，则在串尾追加 ` → {Label}`；
否则原样返回。`null` 进 `null` 出。用于快照的 `Fcs.LeftTask` / `Fcs.RightTask` / `Fcs.PendingTasks`。

---

## 12. 误伤巡逻 `PollFriendlyIntrusions`（队列期持续监视）

**为什么必须有**：入队时的友军普查只反映入队那一刻；前线会移动。已排队任务的弹着区里事后闯入友军
必须被发现。

节流：`5f` 秒一轮，且要求簿记非空、地图已绑定。

每轮：读一次全部地图实体与全部弹种规格（读取失败整轮放弃）。对每个已排队任务：

- 无害弹（见 §14）跳过。
- 取该弹种 `ImpactRadius`（km），`<= 0.001f` 跳过。
- 遍历**存活**实体，判定"友军"：`Role` 含 `"Ally"`，或 `Role == "Spotter"`，或 `Id` 含 `"civil"`
  （忽略大小写），或 `RawId` 含 `"civil"`（忽略大小写）。
- 把实体 map-local 转 km（`10.016 + MapX*3.8164`、`5.235 + MapY*3.8164`），与任务弹着 km 求距离，
  `<= blastKm` 则计入闯入名单。
- 名单非空且该 serial **尚未告警过**（一次性去重）→ 发事件 `friendly_warning` / `map`：
  ```
  ⚠误伤预警: 已排任务 #{serial} {Label} 的弹着区({Shell}半径{blastKm*1000f:F0}m)内现有友军 {逗号分隔的实体Id} — 立即adjust_fire挪开弹着点或cancel_pending_task
  ```
- 名单为空 → 清除该 serial 的告警标记（允许再次告警）。

轮末必须清理告警集合中已不在簿记里的 serial。

---

## 13. 征用点余额后缀 `BalanceSuffix()`

**规则**：余额会随每一次购买变动（FCS 买炮弹、协调器买卡），所以凡是"与购买相邻"的事件都必须
盖上当时余额，让 agent 永远拿新鲜资金做决策。

读到余额时返回 `$" · 征用点余额 {p}"`（注意前导空格 + `·` + 空格），读不到返回空串。

当前使用点：`shell_fired` 事件尾、`requisition`（卡片完成）事件尾。

---

## 14. 无害弹（IFF 豁免名单）

**逐字数组**：`{ "SMK", "STAR", "TEAR", "DRIL" }`，比较忽略大小写。

含义：SMK（遮蔽）、STAR（照明）、TEAR（破隐，零伤害）、DRIL（惰性训练弹）。
**WP 不在名单内**——在其压制/燃烧机制确认无害之前一律走 IFF 检查。

无害弹在下列环节被豁免：`SurveyBlast` 直接返回空后缀、误伤巡逻跳过、盲射警告跳过。

---

## 15. `SurveyBlast` 安全层（爆炸半径普查）

签名语义：输入 弹种、弹着 km、`allowDanger` 开关；输出 `rejection`（非 null 即软/硬拒绝）、
`hostilesInRadius`（覆盖到的敌目标数），返回值是**附加到回执尾部的后缀串**。

流程：

1. 无害弹 → 空后缀、无拒绝、`hostilesInRadius = 0`。
2. 查弹种规格；查不到（弹种为 null/未匹配）→ 空后缀、无拒绝。`ImpactRadius <= 0.001f` 同样直接返回
   （**注意 ImpactRadius 单位是 km**，HE=0.25、HCHE=0.55、AP=0.15）。
3. 遍历**存活**实体，各自算到弹着点的 km 距离，分四类：
   - **平民**：`Id` 含 `"civil"`，或 `RawId` 含 `"civil"`，或 `RawId` 含 `"hospital"`（均忽略大小写）。
     **平民必须按 ID 识别，绝不按阵营 Role 识别**——《白色弹壳》一类关卡故意把难民标成 `role=Enemy`，
     让他们看起来可打。
   - **友军**：非平民 且（`Role` 含 `"Ally"` 或 `Role == "Spotter"`）。
   - 分桶（互斥，按此顺序）：平民且在半径内 → `civiliansInside`；友军且在半径内 → `friendliesInside`；
     友军且在 `1.5 × 半径` 内 → `friendliesNear`；既非平民也非友军且在半径内 → `hostilesCovered`。
4. **平民保护（不可覆盖的硬拒绝）**，优先于一切：`civiliansInside` 非空立即拒绝，
   `allowDangerouslyFriendlyFire` **对平民无效**。
   ```
   平民保护(不可覆盖) — 已拒绝: {名单} 在弹着点km({kmX:F2},{kmY:F2})的{shell}爆炸半径{blastKm*1000f:F0}m内。allowDangerouslyFriendlyFire对平民无效; 换弹着点或换更小半径弹种, 平民不是目标——无论其阵营标注是什么
   ```
   名单条目格式：`{Id}(距弹着{dKm:F2}km)`
5. **友军软拒绝**：`friendliesInside` 非空且未开 `allowDanger` → 拒绝：
   ```
   友军误伤警告 — 已拒绝: {名单} 在弹着点km({kmX:F2},{kmY:F2})的{shell}爆炸半径{blastKm*1000f:F0}m内。用offsetKmX/offsetKmY把弹着点向远离友军一侧移出半径(会牺牲部分毁伤), 或换更小爆炸半径的弹种; 确认接受误伤才用allowDangerouslyFriendlyFire=true重试
   ```
   名单条目格式：`{Id}({Role},距弹着{dKm:F2}km)`
6. 开了 `allowDanger` 且有友军在半径内 → 不拒绝，后缀追加：
   `; 警告: 已确认误伤风险, 友军在爆炸半径内: {名单}`
7. 否则若有友军贴近 → 后缀追加：
   `; 注意: 友军贴近弹着点(≤1.5×爆炸半径): {名单}`，条目格式 `{Id}({dKm:F2}km)`
8. `hostilesInRadius = hostilesCovered.Count`；非空时后缀追加：
   `; 爆炸半径({blastKm*1000f:F0}m)可同时覆盖: {名单}`，条目格式 `{Id}({dKm:F2}km)`
   （目的：让 LLM 能验证一次合并打击真的盖住了它想打的集群。）

拒绝路径返回时后缀丢弃（返回当时已积累的 suffix，实际为空）。

---

## 16. `QueueFireMission(FireMissionRequest)`（主线程）

请求字段（JSON/DTO 名逐字）：`entityId`、`targetPoint`、`bearingDeg`、`distanceKm`、`shell`（默认 `"HE"`）、
`markerId`（默认 4）、`priority`（默认 50）、`validForSeconds`、`offsetKmX`、`offsetKmY`、
`allowDangerouslyFriendlyFire`、`motionFrom`、`motionBearingDeg`、`motionSpeedKmh`、`motionAtTime`。

必须按以下**严格顺序**执行（顺序即协议——早拒绝优先于晚拒绝）：

**0. 前置**：地图未绑定 → 返回 `"tactical map not bound"`。

**1. 目标解析（三选一，优先级 entityId > targetPoint > bearing+distance）**

- `entityId` 非空：在指挥桌上找该实体；找不到 →
  `$"entity '{req.EntityId}' not visible on the command table (fog of war or bad id)"`。
  取实体 map-local 为瞄点，label = entityId。
- `targetPoint` 非空：用**当前棋子位置换算出的 turretKm** 作为相对解析基准，调 `GridMath.ParsePoint`；
  解析失败 → `$"cannot parse target '{req.TargetPoint}' (grid like 'K4 5:0' or 'kmX,kmY')"`。
  km 反算 map-local，label = 原始 targetPoint 串。
- `bearingDeg` 与 `distanceKm` **都**给出：由地图模块把方位/距离解成 map-local；
  label = `$"bearing {bearing:F1}°, {distance:F2} km"`；**标记本次瞄点"派生自炮位"**（影响后面的越界错误文案）。
- 三者都不满足 → `"need entityId, target, or bearingDeg+distanceKm"`。

**2. 偏移限幅**：`|offsetKmX| > 0.5f || |offsetKmY| > 0.5f` →
```
offset exceeds ±0.5km — offsets are for nudging the burst clear of friendlies; aim at different coordinates instead
```
非零偏移则把 km 偏移换成 local（`/3.8164f`）叠加，并把标签追加
`$" 偏移({offX:+0.00;-0.00},{offY:+0.00;-0.00})km"`（正负号必须显式，格式串逐字）。

**3. 越界纵深防御**：把最终 local 转回 km 做 `GridMath.InMapBounds` 校验，失败时按瞄点来源分岔文案：

- 派生自炮位（bearing/distance 路径）：
  ```
  aim point km({kmXCheck:F1},{kmYCheck:F1}) is outside the map — rejected. This aim derives from the ASSUMED turret position + bearing/distance: either the params are wrong, or the assumed turret position is off/OOB — check get_assumed_turret_position and recalibrate if unreliable
  ```
- 绝对坐标路径（entityId / targetPoint，炮位不进数学）：
  ```
  target coordinates km({kmXCheck:F1},{kmYCheck:F1}) are outside the map — rejected. Bad fire params (grid/km parse or triangulation error); the turret position is irrelevant to this path
  ```
  这个分岔是有意的：绝对坐标越界只可能是参数错，不该让模型去怀疑炮位。

**4. 射程校验**：取该弹种装药射程表的最大 `MaxKm`（无表则 `40f`）；**仅当请求显式给了 `distanceKm`** 时校验：
```
distance {dist:F1}km exceeds {req.Shell} max range {maxRange:F1}km — rejected
```

**5. 预算门：火力任务一律不设**。理由必须写进实现注释级别的认知：有的关卡余额为 0 但炮膛已装填
（打已装弹不发生购买），且征用点随时间回补，任何桥侧"买得起吗"的猜测都会误拦。agent 在每份快照里
能看到实时余额；买不起的话在 FCS 侧自然失败。

**6. 安全层**：调 `SurveyBlast`，拿到 `ffRejection` 与 `hostilesInRadius`；有拒绝立即返回拒绝串。

**7. 运动模型转录**（`motionFrom` 非空时）：
- 解析 `motionFrom`，**基准点用地图原点 `(10.016, 5.235)`**（非当前炮位）；失败 →
  `$"cannot parse motionFrom '{req.MotionFrom}'"`
- `motionBearingDeg` 或 `motionSpeedKmh` 缺任一 → `"motionFrom requires motionBearingDeg and motionSpeedKmh"`
- 时间基准 `t0` 默认 = `MissionClockSeconds`；若给了 `motionAtTime`（非空白）则按 24 小时 `"HH:mm"` 或
  `"HH:mm:ss"` 解析（冒号分段 2 或 3 段，各段必须能 `int.TryParse`），失败 →
  ```
  cannot parse motionAtTime '{req.MotionAtTime}' (expect 24h "HH:mm", same clock as event stamps)
  ```
  成功则 `t0 = hh*3600 + mm*60 + ss`（无秒段按 0）。
- 组装 map-local 线性模型：原点 = motionFrom 的 local 坐标，速度 = `(sin(rad)*v, cos(rad)*v)`，
  `v = kmh/3600/3.8164`，时间基准 `t0`。FCS 每规划轮按 `p(t) = origin + vel*(t - t0)` 外推。

**8. 盲射警告（警告不拒绝）**：满足全部条件时才追加——
弹种**非无害弹** 且 `entityId` 为空 且 `hostilesInRadius == 0` 且**无运动模型**：
```
; ⚠盲射警告: {req.Shell}是杀伤弹而弹着半径内无已揭示敌目标——侦察盲射必须用STAR, 校射用DRIL; 只有明确的预判/封锁打击才允许杀伤弹盲射, 否则立即cancel_pending_task省下这笔钱
```
理由：预设的封锁/预判射击是合法的，所以不拒；但"拿杀伤弹当侦察"必须被点名。

**9. 纯瞄点入队（不碰任何物理标记）**：
以当前棋子 local 为原点算出初始方位与距离：
`brg = ((atan2(Δx, Δy) * 57.29578f) % 360f + 360f) % 360f`，`distKm = sqrt(Δx²+Δy²) * 3.8164f`。
把 (瞄点 local, brg, distKm, 弹种, 优先级, 可选 trackEntityId=entityId, 运动模型, validForSeconds)
交给 FCS 网关，取回 FCS 分配的流水号 `serial`。

- 返回值为 `"ok"` 时：
  - `serial > 0` 才建立簿记，`FlightEtaSeconds = distKm / 0.4f + 25f`，`FiredAt` 初始为 `0f`
  - 事件 `fcs_task_update` / `fcs` /
    `$"fire mission queued on {label} ({req.Shell}, P{req.Priority}) as #{serial}"`
  - 返回 `$"ok (#{serial}){suffix}"`
- 否则原样返回 FCS 的错误串（**后缀被丢弃**）。

---

## 17. `AdjustFireMission(AdjustFireRequest)`（最后时刻改瞄，主线程）

语义：对已排队/炮上准备中的任务改瞄。**FCS 从不等待 agent**——不改就按原瞄点发；改了则由 FCS 的
三段重解流水线（pre-aim / pre-fire / manual-wait）在下一轮落实。

请求字段：`serial`（**唯一寻址键，绝不用会回收复用的 targetId**）、`entityId`、`targetPoint`、
`offsetKmX`、`offsetKmY`、`allowDangerouslyFriendlyFire`。

流程：

1. 地图未绑定 → `"tactical map not bound"`
2. 目标解析：只支持 `entityId` 与 `targetPoint` 两条路径（**无 bearing/distance 路径**），
   错误文案与 §16 完全一致；两者都空 → `"need target or entityId"`
3. 偏移限幅与标签追加：与 §16 逐字一致
4. 越界校验（**单一文案，不分岔**）：
   `$"new aim point km({kmXCheck:F1},{kmYCheck:F1}) is outside the map — rejected"`
5. 安全层：**弹种从 FCS 活动集里按 serial 反查**（`TryGetTaskInfo`），用查到的弹种做 `SurveyBlast`；
   有拒绝立即返回
6. 提交改瞄。结果串以 `"ok"` **开头**时：
   - 若簿记里有该 serial，更新它的 `Label`/`KmX`/`KmY`（保持弹着匹配点新鲜）
   - 事件 `fcs_task_update` / `fcs` / `$"#{req.Serial} 瞄准点已调整 → {label}"`
   - **不移动任何物理标记**（T9/T10 由 FCS 的炮位标记循环自动跟随）
7. 返回 `result + suffix`（注意：与 fire 不同，这里失败时后缀**也会**拼上）

---

## 18. 卡片征用 `RequestCard(cardId, bearingDeg, priority=50, startGrid, distanceKm)`

1. **预算门（仅特殊卡）**：能读到余额 **且** 卡片清单里该卡 `Cost > 0` **且** `Cost > 余额` → 拒绝：
   ```
   征用点不足: {cardId} 需{cardInfo.Cost}点, 余额仅{balance}点 — rejected
   ```
   读不到 cost 或读不到余额时**放行**。
2. 优先走 FCS 协调器（DTO 提交到 FCS 的 `ConsoleCardRequest` 优先级队列）。返回非 null 表示 FCS 接管：
   - 事件 `requisition` / `fcs` / `$"card '{cardId}' {viaFcs}"`
   - 返回 `viaFcs + " (result arrives via events)"`
3. FCS 不可用时回退到旧的物理购买路径（`RequisitionOperator.StartPurchase(cardId, bearingDeg, null)`，
   注意 **distanceKm 在回退路径上被丢弃，固定传 null**）。

### 卡片结果回收（2s 节拍）

读 FCS 的"控制台卡片结果"；非空且**与上次记住的不同**时：记住它，发
事件 `requisition` / `fcs` / `$"card request completed: {cardResult}{BalanceSuffix()}"`，
并写事务日志 `TransactionLog.Write("requisition", cardResult)`。
（去重键在 FullReset 时清空。）

---

## 19. 其它公开操作

### `CancelPendingFcsTask(int serial)`
委托 FCS 取消，发事件 `fcs_task_update` / `fcs` / `$"cancel #{serial}: {result}"`，返回 result。

### `PullSignalHorn()`（物理拉响号角，主线程）
1. 在场景里找号角交互件（关键词匹配 horn/signal/siren），同时拿到候选清单；
   找不到 → `"本关场景中没有找到号角装置(无匹配horn/signal/siren的交互件) — 无法发出信号"`
2. 找到但 `isActive == false` → `$"号角 '{horn.gameObject.name}' 当前不可交互 — 可能尚未满足拉响条件"`
3. 调 `OnClickDown()`，并起一个协程在 `WaitForSeconds(0.15f)` 后调 `OnClickUp()`
   （`OnClickUp` 必须 try/catch 吞异常——期间对象可能已销毁）
4. 候选数 > 1 时回执追加 `$" (场景候选: {逗号分隔})"`
5. 事件 `signal` / `game` / `$"号角已拉响: {horn.gameObject.name}{extra}"`
6. 返回 `$"号角已拉响: {horn.gameObject.name}"`（**返回串不含候选清单**，只有事件含）

### `PrintOnTeleprinter(which, string[] lines)`
`which` 等于 `"primary"`（忽略大小写）→ `Teleprinter.Teleprinters.Primary`，
**其余一切值（含拼写错误）都落到 `Secondary`**。返回打印是否成功。

### `ReadTurretLocal()` / `FindVisibleEntity(entityId)`
直通地图模块的薄封装，供 agent 经主线程调用。`FindVisibleEntity` 只返回可见实体
（雾中实体绝不能喂给 LLM——等于开图作弊）。

---

## 20. 快照组装 `BuildSnapshot()`（主线程）

必须产出 `StateSnapshotDto`，字段名逐字（JSON 序列化直出属性名）：

无条件部分：
- `Timestamp` = `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`
- `GameTime` = `EventLog.GameClock`
- `SceneBound` = 地图是否已绑定
- `Teleprinters`（两台全读）、`Guns`（左右两门）、`Fcs`（FCS 状态）、`Cards`（征用台实卡清单）
- `AvailableShells` = `Cards` 的 Id 列表
- `RequisitionPoints`（可空 int）
- `SceneName` = 活动场景名（**仅供诊断**，不可用于模式判别——所有任务都跑在 `Master Turret Scene`），
  取值 try/catch
- `MissionName` = `Il2Cpp.MissionManager.Instance?.CurrentMission?.MissionName?.Get() ?? ""`（本地化显示名，
  关卡情报表的键）；`MissionType` = `mission?.MissionType.ToString() ?? ""`（`MissionGraph.MissionType`：
  Tutorial/Campaign/Challange/Chill，后两者=无尽模式）。整段 try/catch
- `ShellSpecs` = 全量弹种规格 **按 `AvailableShells` 过滤**（忽略大小写的集合过滤）——只给出本关买得到的
- `Fcs.LeftTask` / `Fcs.RightTask` / `Fcs.PendingTasks` 全部经 `AnnotateTask` 加上目标标签
- `InFlightShells` = `DescribeInFlight()`

仅当地图已绑定时才填：
- `MapExtentKm` = `GridMath.MapBoundsText`
- `TurretMapX` / `TurretMapY` = 棋子 map-local 坐标（**local 单位，非 km**）
- `TurretCalibrated`
- `Entities`（可见实体）、`Markers`

---

## 21. `ToggleLlmControl()` 与 `FullReset(reason)`

### `ToggleLlmControl()`
翻转 `AgentConfig.LlmControl`（该 setter 会立刻 `MelonPreferences.Save()`）；
agent 为 null 时到此为止。开且未运行 → 启动；关且在运行 → 停止。
日志 `[AgentBridge] LLM control {ON|OFF}`。

### `FullReset(reason)`
F9 语义的全量重置。**必须**依次做到：

1. 日志 `[AgentBridge] full reset ({reason})`
2. 事务日志 `TransactionLog.Write("reset", $"full reset: {reason}")`
3. 停 agent、清 agent 对话历史
4. **`EventLog.Clear()`** —— 陈旧事件绝不能重放进重启后 agent 的新上下文
5. 清卡片结果去重键、清任务簿记、清在途炮弹清单
6. 解绑地图、`GridMath.ResetMapBounds()`、重置弹着 reader、重置电传 reader
7. 清基线相机（下次绑定重新捕获）、清世界钟缓存、清反炮击"曾在运行"标志
8. `TurretCalibrated = false`、清"上次棋子位置"缓存
9. `_nextBindAttempt = realtimeSinceStartup + 1f`（**1 秒，不是 2 秒**）

**注意 FullReset 不重启 agent**：重启只发生在 `ToggleLlmControl` 或调用方另行处理；
（`UpdateMissionPhase` 的新任务分支只调 FullReset，因此新任务后 agent 保持停止，需 F11 重新开。）

已知触发点：F9 热键、面板"重置"按钮（`FullReset("panel button")`）、进入 `MissionActive`。

---

## 22. 跨模块契约

### 22.1 本模块**暴露**给其他模块

以 `AgentBridgeMod` 实例方法/属性形式（`FdoAgent` 与 `BridgeServer` 均经 `MainThread.Run` 调用，
超时见括号内）：

| 成员 | 消费方 | 说明 |
|---|---|---|
| `BuildSnapshot()` | FdoAgent(15s)、BridgeServer `GET /state` | |
| `QueueFireMission(FireMissionRequest)` | FdoAgent(15s)、`POST /fire` | |
| `AdjustFireMission(AdjustFireRequest)` | FdoAgent(15s)、`POST /adjust` | |
| `CancelPendingFcsTask(int)` | FdoAgent(15s) | |
| `SetDeclaredTurret(float,float)` | FdoAgent(15s)、`POST /turret` | |
| `RequestCard(string,float?,int,string?,float?)` | FdoAgent(15s) | |
| `PullSignalHorn()` | FdoAgent(10s)、`POST /horn` | |
| `PrintOnTeleprinter(string,string[])` | `POST /print` | |
| `ReadTurretLocal()` | FdoAgent(10s) | 返回 `UnityEngine.Vector3`（map local） |
| `FindVisibleEntity(string)` | FdoAgent(10s) | |
| `DescribeInFlight()` | 快照 | |
| `LastFcsSummary`（只读属性） | AgentWindow 面板 | |
| `TurretCalibrated`（只读属性） | 快照 | |
| `ToggleLlmControl()` / `FullReset(string)` | AgentWindow 面板按钮 | |

静态字段（后台线程直接读，必须 `volatile`）：

| 成员 | 消费方 |
|---|---|
| `public static volatile bool CinematicActive` | FdoAgent 主循环暂停判据；OnGUI 面板门 |
| `public static volatile bool GameFocused` | FdoAgent 主循环暂停判据 |
| `public static volatile float MissionClockSeconds` | 本模块运动模型 `t0` 默认值 |

FdoAgent 暂停逻辑对应文案（属 agent 模块，此处仅为契约参照）：
`paused (cinematic)` / `paused (game unfocused)`。

### 22.2 本模块**依赖**的其他模块

- `MainThread`：`Pump()`（每帧）、`Run<T>` / `Run(Action)`（默认 10s 超时，同步）、`Post`（fire-and-forget）
- `EventLog`：`Append(type, source, text, data?)`、`Clear()`、`GameClock`（本模块是唯一写者）
- `Agent.TransactionLog.Write(type, text, data?)`
- `AgentConfig`：`Initialize()`、`EnableHttpApi`（只读）、`LlmControl`（读写，写即落盘）
- `Agent.GridMath`：`SetMapBoundsKm(minX,minY,maxX,maxY)`、`ResetMapBounds()`、
  `InMapBounds((x,y))`、`ParsePoint(string, (x,y) turretKm)`、`MapBoundsText`
- `MapReader`：`IsBound`、`MapSurface`、`KmBounds`（可空四元组）、`TryBind()`、`Unbind()`、
  `TurretLocalOnMap()`、`SolutionToMapLocal(bearing, distKm)`、`SetDeclaredTurret(kmX,kmY)`、
  `FindEntity(id)`、`ReadEntities()`、`ReadMarkers()`、`PollAndEmitEvents()`
- `ImpactReader`：`Reset()`、`PollAndEmitEvents(Transform? mapSurface, Func<float,float,string?> resolveImpact)`
- `TeleprinterReader`：`Reset()`、`ReadAll()`、`Print(Teleprinter.Teleprinters, IEnumerable<string>)`、
  `PollAndEmitEvents()`
- `Fcs.FcsGateway`：`ReadStatus()`、`GetRequisitionLock()`、`RequestCardPurchase(cardId, bearingDeg, priority, startGrid, distanceKm)`、
  `ReadConsoleCardResult()`、`EnqueueAimPoint(localX, localY, bearingDeg, distanceKm, shell, priority, out serial, trackEntityId, MotionSpec?, validForSeconds)`、
  `AdjustTaskAim(serial, localX, localY)`、`TryGetTaskInfo(serial, out shell, out markerId)`、`CancelPending(serial)`；
  记录类型 `FcsGateway.MotionSpec(OriginLocalX, OriginLocalY, VelLocalX, VelLocalY, T0Seconds)`
- `GameState.AmmoReader`：`ReadRequisitionPoints()`（`int?`）、`ReadCards()`、`ReadShellSpecs()`
- `GameState.GunStateReader.ReadBoth()`
- `GameState.RequisitionOperator`：静态字段 `RequisitionLockProvider`（`Func<object?>?`）、
  `StartPurchase(cardId, bearingDeg, distanceKm)`
- `GameState.SignalOperator.FindHorn(out List<string> candidates)` → `LookAtTarget?`
- `Http.BridgeServer`：`const int Port = 17171`、`Start()`、`Stop()`
- `Ui.AgentWindow`：字段 `Visible`、`Draw(FdoAgent, AgentBridgeMod)`
- `Agent.FdoAgent`：构造接收本模块实例；`IsRunning`、`Start()`、`Stop()`、`ClearLog()`

### 22.3 游戏侧（Il2Cpp）反射/直接引用的类型与成员（逐字）

| 类型 | 成员 |
|---|---|
| `GenericTimerSceneSync` | `TimerID`、`CurrentTime`（当日秒数） |
| `MissionStatsTracker` | `Instance`、`timerRunning`、`timerValue` |
| `CounterBatteryTimer` | `Instance`、`IsRunning`、`IsExpired`、`IsPermanentlyStopped`、`TimeRemaining` |
| `MissionManager` | `Instance`、`CurrentPhase`、枚举 `MissionManager.GamePhase`（值 `MissionActive`）、`CurrentMission` |
| `MissionGraph`（经 `CurrentMission`） | `MissionName.Get()`、`MissionType` |
| `Teleprinter` | 枚举 `Teleprinter.Teleprinters`（`Primary` / `Secondary`） |
| `LookAtTarget` | `isActive`、`OnClickDown()`、`OnClickUp()`、`gameObject.name` |
| `UnityEngine.Camera` | `main`、`name` |
| `UnityEngine.Object` | `FindObjectsOfType<T>()` |
| `UnityEngine.Application` | `isFocused` |
| `UnityEngine.Time` | `realtimeSinceStartup` |
| `UnityEngine.SceneManagement.SceneManager` | `GetActiveScene().name` |
| `UnityEngine.InputSystem.Keyboard` | `current`、`f9Key` / `f10Key` / `f11Key`、`wasPressedThisFrame` |
| `MelonCoroutines` | `Start(IEnumerator)` |

### 22.4 事件类型 / 来源枚举（本模块产出的部分，逐字）

| type | source | 触发 |
|---|---|---|
| `cinematic` | `game` | CG 开始/结束 |
| `counter_battery` | `game` | 反炮击启动 / 20s 播报 / 归零 / 永久解除 |
| `signal` | `game` | 号角拉响 |
| `shell_fired` | `fcs` | 任务离开活动集且无失败记录 |
| `fcs_task_update` | `fcs` | 入队成功 / 任务失败(未发射) / 改瞄 / 取消 |
| `requisition` | `fcs` | 卡片入队回执、卡片购买完成 |
| `shell_impact` | `map` | 超时销账（真实弹着事件由 ImpactReader 发） |
| `friendly_warning` | `map` | 已排任务弹着区闯入友军 |
| `turret_position` | `map` | 手动拖动检测、`SetDeclaredTurret` 结果 |

事务日志类型：`mission`、`requisition`、`reset`。

### 22.5 相关 HTTP 端点（由 BridgeServer 暴露，本模块是其后端）

`GET /state`（`BuildSnapshot`）、`POST /fire`、`POST /adjust`、`POST /turret`（`{kmX, kmY}`）、
`POST /horn`、`POST /print`（`{which, lines}`，`which` 缺省 `"secondary"`）。
监听 `127.0.0.1:17171`。

---

## 23. 不变量与防御性规则（必须逐条落实）

1. **主线程独占**：一切 Il2Cpp/Unity 访问只能在 `OnUpdate` 泵内发生。后台线程一律经 `MainThread.Run`
   （需要结果、同步、有超时）或 `MainThread.Post`（装饰性、绝不阻塞 agent）。本模块所有 public
   操作方法（`BuildSnapshot`/`QueueFireMission`/`AdjustFireMission`/`SetDeclaredTurret`/`RequestCard`/
   `CancelPendingFcsTask`/`PullSignalHorn`/`PrintOnTeleprinter`/`ReadTurretLocal`/`FindVisibleEntity`）
   都是"仅主线程"契约。
2. **Il2Cpp 访问必须 try/catch**：任何游戏侧单例/属性读取都可能在场景切换、对象销毁、类型未加载时抛异常。
   0.5s 杂项节拍的五项**必须各自独立** try/catch；2s FCS 节拍整块 catch；地图/电传轮询各自 catch 并记
   Warning；弹着轮询 catch **静默**；热键块整块 catch。
3. **异常绝不冒泡出 MelonMod 回调**——一次未捕获异常会让 MelonLoader 卸掉整个 mod。
4. **FCS Logic 在可回收 ALC 里**：不得持有对 FCS 逻辑对象的强引用；F9/热重载后所有反射链必须重解析。
   本模块只经 `FcsGateway` 间接接触。
5. **绝不移动任何地图标记**：T1–T8 归玩家手动，T9/T10 归 FCS 自动控制。入队/改瞄全走纯坐标路径。
6. **绝不解析 FCS 的显示字符串**来判定任务状态：出膛判定只认结构化的 `SerialToMarker` 键集合，
   失败判定只认 `RecentOutcomes`。唯一允许的正则是从**自己生成的**显示串里抠 `#N` 做标注（§11.1）。
7. **`#N` 是唯一任务寻址键**：`targetId`（=标记号）会回收复用必然重复，不得用于对外显示或寻址。
8. **平民保护不可覆盖**：`allowDangerouslyFriendlyFire` 只能覆盖友军部队自愿承担的风险，
   对平民永远无效——无论游戏或统帅部把他们标成什么阵营。
9. **平民按 ID 识别，不按 Role**（`civil` / `hospital` 子串，忽略大小写）。
10. **`ImpactRadius` 单位是 km**：任何对人展示都要 `*1000` 转米。曾因按米处理导致"爆半径 0m"、
    友军拦截形同虚设。
11. **雾中实体绝不外泄**：只有 `Visible` 的实体能进快照/工具回执。
12. **在途炮弹绝不静默过期**：超时必须发 `shell_impact` 事件销账。
13. **静态可变状态必须 `volatile`**：`CinematicActive`、`GameFocused`、`MissionClockSeconds`、
    `EventLog.GameClock`。
14. **编码陷阱**：源文件含大量中文常量。**绝不用 PowerShell `Get-Content`/`-replace`/`Set-Content`
    改动这些文件**——中文 Windows 会用 GBK 误读 UTF-8 再回写导致全文乱码。只用 UTF-8 安全的编辑方式。
15. **配置陷阱**：`MelonPreferences.cfg` 在游戏运行中被按内存值整文件重写，运行期手改必被清。
    运行期改开关只能走热键/面板（`AgentConfig.LlmControl` 的 setter 会立刻 Save）。
16. **agent 永不自动启动**：只有 F11 / 面板按钮 / 显式 `ToggleLlmControl` 能启动它。
17. **绑定重试是幂等的**：未绑定时每 2 秒尝试一次，绑定成功后停止尝试；FullReset 后 1 秒重试。

---

## 24. 逐字保留数据块

本模块（`AgentBridgeMod.cs`）**不含**大段自然语言数据块（SystemPrompt / MapIntelTable / 学说文本均在
`agent/FdoAgent.cs`，属其他模块）。本节内出现的所有中文串都是**协议级消息格式**，已在上文逐字给出，
必须原样搬运，不得改写、不得翻译、不得调整标点（含 `⚠`、`—`、`·`、全角括号与中英混排）。

---

## 25. 待澄清 / 疑似缺陷（重实现前请裁决）

见返回的 `openQuestions`。
