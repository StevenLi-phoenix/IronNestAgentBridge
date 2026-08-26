# 模块需求：http-api（本地 HTTP 控制面 + 事件日志 + 长轮询）

来源文件：`Http/BridgeServer.cs`、`Dtos.cs`、`EventLog.cs`（并对照 `MainThread.cs`、
`AgentBridgeMod.cs`、`GameState/*`、`agent/AgentConfig.cs`、`agent/FdoAgent.cs` 校验契约）。

本模块的职责：把 mod 内部的战场状态与操作能力，以**仅监听回环地址**的 JSON HTTP API 暴露给
外部 agent/调试工具；并提供一个进程内全局事件日志（环形缓冲 + 长轮询），既服务 HTTP 客户端，
也服务内置 agent 线程。

---

## 1. 服务器生命周期与绑定

- 端口常量 **17171**（`BridgeServer.Port`，public const int）。前缀必须且只能是
  `http://127.0.0.1:17171/`。**绝不监听 `+`/`*`/`0.0.0.0`**：这是玩家自己 agent 的本地控制面，
  不是网络服务；且端点能开炮、花征用点、在地图上作画。
- 是否启动由 MelonPreferences 配置门控：分类 `AgentBridge`，键 **`EnableHttpApi`**，
  类型 bool，**默认 `false`**，description 逐字为：
  `Expose the local debug HTTP API (fire/draw/requisition endpoints). Keep OFF unless developing — RCE surface for local processes.`
  - 关时必须打印 `[AgentBridge] HTTP API disabled (EnableHttpApi=false)`（Msg 级）。
  - 开时在 mod 初始化（`OnInitializeMelon`）里创建并启动；启动抛异常必须捕获并以 Error 级打印
    `[AgentBridge] failed to start HTTP server on port {Port}: {ex.Message}`，且**不得让 mod 初始化失败**。
  - 启动成功打印 `[AgentBridge] HTTP API listening on http://127.0.0.1:17171/`。
- 必须开 **4 条**后台监听线程，`IsBackground = true`，线程名 `AgentBridge-http-{i}`（i=0..3）。
  4 条是硬需求：`GET /events` 长轮询会占住一条线程最长 60 秒，单线程会让其余端点全部饿死。
- 停止（`OnDeinitializeMelon`）：先置停止标志（`volatile bool`），再 `listener.Stop()`，
  Stop 抛异常必须吞掉。监听循环里 `GetContext()` 抛异常 = 监听器已关，该线程**直接结束**（不重试、不刷日志）。
- 每个请求的处理必须整体包 try/catch：失败以 Warning 打印
  `[AgentBridge] request failed: {ex.Message}`，并回 **500** `{"error":"<ex.Message>"}`。

## 2. JSON 编解码约定（协议本体）

请求与响应共用同一套 `JsonSerializerOptions`，重实现必须保持等价行为：

- `PropertyNamingPolicy = CamelCase`：**所有响应字段名为 camelCase**（DTO 的 C# PascalCase 属性
  在线上一律首字母小写，如 `MapX` → `mapX`、`RequisitionPoints` → `requisitionPoints`）。
  匿名对象响应同样走 camelCase。
- `PropertyNameCaseInsensitive = true`：请求体字段名大小写不敏感（`kmX` / `KmX` / `kmx` 均可）。
- `DefaultIgnoreCondition = WhenWritingNull`：**值为 null 的字段整个从响应里消失**（例如
  `requisitionPoints`、`leftTask`、`rightTask`、`missionName`、`mapExtentKm`）。客户端必须按
  "字段可缺失" 处理，不得依赖 null 字面量。
- `WriteIndented = false`。
- `Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping`：**不得改**。绝大多数事件文本与
  错误消息是中文，默认编码器会把它们转成 `\uXXXX`，人肉调试与 LLM 上下文都会被污染。
- 响应写出：`Content-Type: application/json; charset=utf-8`，必须显式设置 `ContentLength64`，
  写完 `Close()`。写出过程中的任何异常必须**静默吞掉**（客户端断开是常态，不得因此打日志或抛出）。
- 请求体读取：固定 **UTF-8** 解码整个 `InputStream`，反序列化到目标类型。
  **任何**异常（空体、非法 JSON、类型不匹配）一律降级为 `null` 返回，由端点决定回什么 400。

## 3. 路由规则

- 路径 = `Request.Url.AbsolutePath`，**去掉结尾的 `/`**（`TrimEnd('/')`）；Url 为 null 时按空串处理。
  故 `/state` 与 `/state/` 等价；`/` 归一化为空串，落入 404。
- 匹配是 `(method, path)` 的**精确、大小写敏感**匹配。`GET /State` = 404。
- 除 `GET /events`（`since`、`timeoutMs`）与 `GET /find`（`q`）外，查询串一律忽略。
- 未命中回 **404**，响应体逐字为：
  ```json
  {"error":"unknown endpoint","endpoints":["GET /state","GET /events?since=N","POST /fire","POST /print"]}
  ```
  （注意该清单是**残缺**的历史遗留，只列 4 个端点；见"未决问题"。）
- 无认证、无 CORS 头、无速率限制、无请求体大小限制 —— 全靠"只绑回环 + 默认关闭"这一道防线。

## 4. 端点清单（路径与字段名逐字）

所有触碰游戏对象的端点都必须经主线程编组（见 §7）。下表"主线程超时"列为 `MainThread.Run`
的超时毫秒数。

| 方法 | 路径 | 主线程超时 | 成功码 |
|---|---|---|---|
| GET | `/state` | 10000 | 200 |
| GET | `/events` | 不入主线程 | 200 |
| POST | `/fire` | 10000 | 见下（现状恒 409） |
| GET | `/markers` | 10000 | 200 |
| POST | `/draw` | 10000 | 200 |
| POST | `/turret` | 10000 | 200 |
| POST | `/requisition` | **15000** | 200 |
| GET | `/find` | 10000 | 200 |
| GET | `/console` | 10000 | 200 |
| POST | `/adjust` | 10000 | 200 |
| POST | `/command` | 不入主线程 | 200 |
| POST | `/horn` | 10000 | 200 |
| POST | `/scoutplane` | 10000 | 200 |
| POST | `/draw/clear` | 10000 | 200 |
| POST | `/print` | 10000 | 200/409 |

### 4.1 `GET /state`

- 无参数。主线程上构建快照（`AgentBridgeMod.BuildSnapshot()`），整体作为响应体。
- 响应 = `StateSnapshotDto`，字段（camelCase）：
  `timestamp`(long, Unix 毫秒 UTC)、`gameTime`(string，快照时刻的任务钟，与事件时间戳同轴)、
  `sceneBound`(bool)、`turretMapX`/`turretMapY`(float，地图 local 空间)、`turretCalibrated`(bool)、
  `entities`([MapEntityDto])、`markers`([MarkerDto])、`teleprinters`([TeleprinterDto])、
  `guns`([GunDto])、`fcs`(FcsStatusDto)、`availableShells`([string])、`cards`([CardDto])、
  `requisitionPoints`(int?，可缺失)、`sceneName`(string?)、`missionName`(string?)、
  `missionType`(string?)、`shellSpecs`([ShellSpecDto])、`inFlightShells`([string])、
  `mapExtentKm`(string?)。
- `sceneBound=false` 时 `entities`/`markers` 为空数组、`turretMapX/Y` 为 0、`mapExtentKm` 缺失
  —— 客户端必须先看 `sceneBound` 再信坐标。

DTO 字段（全部为线上契约，字段名不可改）：

- **MapEntityDto**：`id`、`rawId`、`role`、`roleValue`(int)、`state`、`stateValue`(int)、
  `health`、`maxHealth`、`armour`、`stars`、`isAlive`、`visible`、`immuneShells`(string[])、
  `mapX`、`mapY`、`bearingDeg`、`distanceKm`。
  - `mapX/mapY` 是**战术图 local 空间**（与可拖动标记同一空间），不是 km。
  - `bearingDeg`/`distanceKm` 是相对炮位的**估算**射击诸元，只供态势感知；权威解算另有其道。
  - `visible=false` 的实体**绝不能**进入给 LLM 的视图（开图作弊），但 `/state` 本身只回可见实体
    （事件轮询内部才读全量）。
- **MarkerDto**：`id`(int)、`mapX`、`mapY`、`bearingDeg`、`distanceKm`。
- **TeleprinterDto**：`which`（**只取 `"primary"`/`"secondary"`** 两个值；primary=最高统帅部，
  secondary=战场报告）、`bound`(bool)、`fullText`（已剥离富文本标签的整卷文本）。
- **GunDto**：`side`、`bound`、`chamberedShell`(string?，可缺失)、`powderCharges`(int)、
  `canFire`、`isReloading`、`currentElevation`(float, 度)。
- **FcsStatusDto**：`modPresent`、`logicLoaded`、`bound`、`pendingCount`、`leftTask`(string?)、
  `rightTask`(string?)、`autoFireEnabled`、`maxChargeEnabled`、`pendingTasks`([string])、
  `completedTaskCount`、`successfulTaskCount`、`failedTaskCount`、
  `serialToMarker`(**Dictionary<int,int>** → JSON 对象，键为字符串化的流水号 `#N`，值为内部
  地图标记 id)、`recentOutcomes`(**Dictionary<int,string>**，值形如 `"Finished"` 或
  `"Failed: <原因>"`)。
  - 不变量：**显示串只带 `#N`，永远不许被解析**；序号→标记的对应只走 `serialToMarker` 结构化字段。
  - `recentOutcomes` 是"某流水号从活动集合消失"时区分**已发射**与**任务失败**的唯一依据。
- **ShellSpecDto**：`id`、`damage`(int)、`impactRadius`(**单位 km**，HE=0.25、HCHE=0.55、
  AP=0.15；历史事故：曾按米处理导致爆半径显示 0m、友军拦截形同虚设)、`projectilesPerShell`、
  `maxCharges`、`chargeRanges`([ChargeRangeDto])。
- **ChargeRangeDto**：`charge`(int)、`minKm`、`maxKm`。
- **CardDto**：`id`、`cost`(int)、`remainingUses`(int)、`isRecon`(bool)。

### 4.2 `GET /events`（长轮询，见 §6 语义）

- 查询参数：
  - `since`（long）：只回 `seq > since` 的事件。**缺失或解析失败时默认取当前 `LatestSeq`**
    —— 即"只要将来的"，不重放历史。这是刻意行为，重实现不得改成默认 0。
  - `timeoutMs`（int）：钳位到 **[0, 60000]**；缺失或解析失败时默认 **25000**。
- 响应 200：`{"latest": <long>, "events": [BridgeEvent...]}`。
  `latest` 在等待**返回之后**读取，代表日志当前最新序号，客户端可用它做游标自愈。
- 该端点**不进主线程**，即使游戏暂停/失焦/加载场景也必须正常长轮询。

**BridgeEvent** 字段：`seq`(long)、`timestamp`(long, Unix 毫秒 UTC)、`type`(string)、
`source`(string)、`text`(string)、`gameTime`(string，追加时刻的游戏钟，无钟时为空串)、
`data`(object?，可缺失)。

### 4.3 `POST /fire`

- 请求体 = **FireMissionRequest**，字段：
  `entityId`(string?)、`targetPoint`(string?，网格 `"K4 5:0"` 或 `"kmX,kmY"`)、
  `bearingDeg`(float?)、`distanceKm`(float?)、`shell`(string，**默认 `"HE"`**)、
  `markerId`(int，默认 **4**)、`priority`(int，默认 **50**，0-100，**≥90** 跳过 FCS 凑单窗口
  并优先抢炮)、`validForSeconds`(float?，队列有效期秒；null/0 = 永久有效)、
  `offsetKmX`/`offsetKmY`(float?，km，**上限 ±0.5**)、
  `allowDangerouslyFriendlyFire`(bool)、
  `motionFrom`(string?)、`motionBearingDeg`(float?)、`motionSpeedKmh`(float?)、
  `motionAtTime`(string?，24 小时制 `"HH:mm"`，默认"现在")。
  - 目标解析优先级：`entityId` > `targetPoint` > `bearingDeg`+`distanceKm`；三者皆无 = 拒绝。
  - `markerId` 是历史遗留字段：现行入队走纯坐标路径，不再征用任何物理标记（见"未决问题"）。
- 请求体不可解析 → **400** `{"error":"invalid JSON body"}`。
- 否则主线程执行入队，响应 `{"result": "<字符串>"}`；状态码规则为
  **`result` 恰好等于 `"ok"` 时 200，否则 409**。
  - 现状：成功路径返回的是 `ok (#N)…`，恒不等于 `"ok"`，因此**成功也回 409** —— 见"未决问题"。
    重实现必须明确取舍：要么按 `result` 前缀 `ok` 判 200，要么把成功码固定 200。

### 4.4 `POST /adjust`

- 请求体 = **AdjustFireRequest**：`serial`(int，任务唯一流水号 `#N`；**不是**可回收复用的 targetId)、
  `entityId`(string?)、`targetPoint`(string?)、`offsetKmX`/`offsetKmY`(float?，语义与上限同 fire)、
  `allowDangerouslyFriendlyFire`(bool)。
- 体不可解析 → 400 `{"error":"need {serial, target|entityId, offsetKmX?, offsetKmY?}"}`
  （注意错误文案里写的是 `target`，而实际字段名是 `targetPoint` —— 文案与字段不一致，见"未决问题"）。
- 否则 200 `{"result": "<字符串>"}`，**业务失败也是 200**，靠 result 文本判断。

### 4.5 `POST /print`

- 请求体：`{"which": "primary"|"secondary", "lines": ["...", ...]}`。
- 体为 null、`lines` 为 null 或空数组 → 400 `{"error":"need {which, lines[]}"}`。
- `which` 缺省为 **`"secondary"`**；匹配规则是"忽略大小写等于 `primary` 才走 primary，
  其余一切值都落到 secondary"。
- 成功 200 `{"result":"ok"}`；打印机不可用 **409** `{"result":"printer not available"}`。

### 4.6 `POST /command`（指挥官口头直令）

- 请求体：`{"text": "..."}`。`text` 缺失/空白 → **400**，错误文案逐字：
  `need {text} — 指挥官口头直令, 权威高于统帅部电文`
- 成功：把 **trim 后**的文本作为事件写入日志，类型 **`commander_order`**，来源 **`commander`**，
  然后回 200，响应体逐字：`{"result":"ok — 直令已下达"}`。
- **不进主线程**：直令必须在游戏暂停/失焦时也能送达。
- 权威层级（学说约定，事件消费方遵守）：**指挥官直令 > 统帅部电文 > 战场报告**。
- 编码陷阱：Windows 下 `curl -d` 内联中文会被转成 GBK 乱码；中文体必须写 UTF-8 文件后
  `--data-binary @file`。服务端只能保证按 UTF-8 解码，纠正不了客户端。

### 4.7 `POST /turret`

- 请求体：`{"kmX": <float>, "kmY": <float>}`（km 帧坐标）。体不可解析 → 400
  `{"error":"need {kmX, kmY}"}`。
- 200 `{"result": "<字符串>"}`；越界/哨兵值等业务拒绝仍是 200，文本里带 `rejected`。
- 注意：`{}` 是合法 JSON，会解析出 `kmX=0,kmY=0` 并被当成真实请求提交（下游按越界拒绝）。

### 4.8 `POST /requisition`

- 请求体：`{"cardId": "<卡 ID>", "bearingDeg": <float?>}`。`cardId` 缺失 → 400
  `{"error":"need {cardId, bearingDeg?}"}`。
- 主线程超时**加长到 15000ms**（物理购买要插卡、拨盘、等按钮）。调用形如
  `StartPurchase(cardId, bearingDeg, null)` —— **本端点无法传 distanceKm**（第三参恒 null），
  故 `MoveDirection` 这类需要距离拨盘的卡走不通此路。
- 200 `{"result": "<字符串>"}`，典型值 `started (physical purchase takes ~4s; watch events for the outcome)`
  或 `card '<id>' not on the console; available: [...]` 或 `requisition operator busy with a previous card`。
  真正结果**异步**经 `requisition` 事件回来。

### 4.9 `POST /horn`

- 无请求体。200 `{"result": "<字符串>"}`（成功文本形如 `号角已拉响: <对象名>`；
  无号角/不可交互时也是 200 + 中文说明）。

### 4.10 `POST /scoutplane`

- 请求体：`{"kmX": <float>, "kmY": <float>, "bearingDeg": <float>}`。体不可解析 → 400
  `{"error":"need {kmX, kmY, bearingDeg}"}`。
- 200，响应是**未包 `result` 包装的原始对象**：
  成功 `{"result":"ok","templateName":"<节点名>","world":{"x":..,"y":..,"z":..},"components":[..]}`，
  失败 `{"error":"<原因>"}`（**仍是 200**）。
- 方位约定：bearing 0 = 地图正北（local +Y），顺时针增大。

### 4.11 `GET /markers`

- 无参数。200，原始对象：
  ```
  {"placers":[{"name":..,"path":..,"active":bool,"prefabs":[string],"placed":int}],
   "captured":[{"placerIndex":int,"prefabName":str,"origin":{"x":..,"y":..},"target":{"x":..,"y":..}}]}
  ```
  捕获失败时 `captured` 里塞一个 `{"error":"<msg>"}` 元素（不是顶层错误）。

### 4.12 `POST /draw` 与 `POST /draw/clear`

- `/draw` 请求体：`{"placerIndex": int, "prefabName": string, "ox": float, "oy": float,
  "tx": float, "ty": float}`。体为 null 或 `prefabName` 为 null → 400
  `{"error":"need {placerIndex, prefabName, ox, oy, tx, ty}"}`。
- 语义：origin=(ox,oy)、target=(tx,ty)，**坐标即 km 帧**（存档标记坐标与 km 帧实测等价）。
  点 = 零长度笔画（origin==target）。prefab 名如 `MapMarkerRED`/`MapMarkerYellow`/
  `MapMarkerWhite`（笔）、`MapMarkerDiscCompass`（圆规：origin=圆心、target=半径端点）。
- 200 `{"result": "ok" | "no MapMarkerPlacer in scene" | "draw failed: <msg>"}`。
- `/draw/clear`：无请求体，200 `{"result":"cleared markers on <N> placer(s)"}`。
  **会连玩家手绘一起清掉**，这是端点的既定语义。

### 4.13 `GET /find`（调试）

- 参数 `q`：名称子串，**长度 < 3 直接 400**，文案 `{"error":"need ?q=<name substring, >=3 chars>"}`。
- 200 原始对象 `{"count": int, "hits": [{"path": str, "active": bool,
  "world": {"x":..,"y":..,"z":..}, "mapLocal": {"x":..,"y":..,"kmX":..,"kmY":..}}]}`。
  `hits` 上限 **60** 条（截断，不分页、不提示被截断）；场景里没有 `Draggable Surface` 时
  `mapLocal` 为 null（因 WhenWritingNull 而**整个字段消失**）。
- 匹配大小写不敏感。

### 4.14 `GET /console`（调试）

- 无参数。200，响应是一个**数组**（不是对象），逐根遍历 `"Requisition Console"` 与
  `"Console Box"` 两个根对象：命中回 `{"root": "<名>", "nodes":[{"path":..,"comps":[string]}]}`，
  未命中回 `{"root":"<名>","error":"not found"}`。深度上限 6 层。

## 5. 事件日志（EventLog）需求

- 全局静态、线程安全的**环形缓冲**：容量常量 **2048**。超出时从**头部**丢弃到剩 2048 条。
- 序号 `seq` 从 **1** 起单调递增，**永不回绕、永不复用**；`Clear()` **不重置序号**。
- 追加接口语义：`Append(type, source, text, data = null)`，必须在同一把锁内完成
  "分配 seq → 入队 → 裁剪 → 唤醒所有等待者"。
  - `timestamp` = 追加时刻的 Unix 毫秒 UTC。
  - `gameTime` = 追加时刻的 `GameClock` 快照（见下）。
- `GameClock`：**volatile string**，由 mod 更新循环刷新。
  - 有游戏内 24 小时世界钟时格式 **`"HH:mm"`**（世界钟秒数 → `(t/3600)%24 : (t/60)%60`）。
  - 无世界钟时回退到任务秒表，格式 **`"mm:ss"`**（`t/60 : t%60`）。
  - 未开钟时为**空串**；消费方必须容忍空串。
  - 这是"电文时刻 / 事件时刻 / 快照 gameTime / 工具回执 `[@HH:mm]`"共用的同一根时间轴。
- `LatestSeq`：加锁读 `nextSeq - 1`。空日志时为 0。
- `Clear()`：清空缓冲并唤醒所有等待者，序号继续。用于全量重置（F9 / 新任务开始）——
  **陈旧事件绝不能重放进重启后的 agent 上下文**；电文与地图轮询会在重新绑定后按现状重新产出事件。

### 5.1 事件类型 / 来源全表（`type` × `source`，逐字）

| type | source | 触发与文本要点 |
|---|---|---|
| `telegraph_message` | `primary` / `secondary` | 电文卷新增的增量文本（已剥富文本标签）。整卷被替换/清空时发全文 |
| `entity_revealed` | `map` | `{id} ({role}) revealed at bearing {bearing:F1}°, {dist:F2} km`，`data` = MapEntityDto |
| `entity_moved` | `map` | `{id} moved to bearing {bearing:F1}°, {dist:F2} km`，`data` = MapEntityDto |
| `entity_damaged` | `map` | `{id} damaged: {health}/{maxHealth}`，`data` = MapEntityDto |
| `entity_destroyed` | `map` | `{id} destroyed`，`data` = MapEntityDto |
| `fcs_task_update` | `fcs` | 入队 `fire mission queued on {label} ({shell}, P{priority}) as #{serial}`；取消 `cancel #{serial}: {result}`；改瞄 `#{serial} 瞄准点已调整 → {label}`；任务失败 `⚠任务失败(未发射): #{serial} …` |
| `shell_fired` | `fcs` | `炮弹出膛: #{serial} {label} ({shell}) 已在飞行途中, 等待弹着 — 勿重复排队该目标{余额后缀}` |
| `shell_impact` | `map` | 实测弹着 `实际弹着({gunName}): km(x,y) [网格]`（可带 ` → 在途任务 #N … 已落地销账`）；超时推定 `弹着推定: #N … 已超预计飞行时间…` |
| `impact_hint` | `map` | 命中 `弹着确认: … 命中(爆炸半径内有目标, 无修正提示)`；脱靶黄箭头 `弹着修正提示(黄箭头): …方位约 （不准确）{deg:F0}° 方向, 距离（不准确）"{range}"` |
| `friendly_warning` | `map` | `⚠误伤预警: 已排任务 #{serial} … 半径{m}m …内现有友军 … — 立即adjust_fire挪开弹着点或cancel_pending_task` |
| `requisition` | `fcs` / `console` | fcs：`card '{id}' {结果}`、`card request completed: {结果}{余额后缀}`；console：`requisition card '{id}' -> {结果}`、`scout bearing set: requested {b:F1}°, applied {a:F1}°` |
| `turret_position` | `map` | `turret piece was moved manually — treated as calibrated`；或 SetDeclaredTurret 的返回文本 |
| `counter_battery` | `game` | 启动/每 20s/归零/永久解除四种中文文本（时间格式 `mm:ss`） |
| `cinematic` | `game` | `cinematic started` / `cinematic ended` |
| `signal` | `game` | `号角已拉响: {对象名}[ (场景候选: …)]` |
| `scout_plane` | `map` | `scout plane launched at km(x,y) bearing {deg:F0}°` |
| `commander_order` | `commander` | **本模块唯一自产事件**，文本 = `/command` 的 text（trim 后） |

- `余额后缀` 统一格式：` · 征用点余额 {N}`（读不到余额时为空串）。
- agent 内部还会**合成**一条不入日志的伪事件：`type="recheck"`、`source="agent"`、
  text `定时复查: 无新事件, 重新评估当前战场态势` —— HTTP 客户端永远不会看到它。
- `Dtos.cs` 里 `BridgeEvent.Type` 的注释只列了 6 种类型，是**过时注释**，以上表为准。

## 6. 长轮询语义（必须逐条满足）

1. `WaitForEvents(since, timeoutMs)`：返回 `seq > since` 的**全部**事件（按 seq 升序，即插入序）。
2. 有存量立即返回，不等待。
3. 无存量则阻塞等待，直到有新事件被追加（`Append`/`Clear` 唤醒）或超时。
4. 超时判定用**单调时钟**（`Environment.TickCount64`）算的绝对 deadline，
   等待必须切成 **≤1000ms 的片**循环 —— 保证漏掉的脉冲最多延迟 1 秒被发现，并让停机/清空及时生效。
5. 超时返回**空列表**（不是 null、不是错误）。HTTP 层照样回 200 `{"latest":N,"events":[]}`。
6. `timeoutMs = 0` 必须退化为**非阻塞抽干**（agent 的"随查搭车"依赖这一点）。
7. HTTP 层对 `timeoutMs` 钳位 [0, 60000]，缺省 25000；`since` 缺省 = 当前 `LatestSeq`。
8. **游标推进由客户端负责**：正确用法是把上一次响应里最后一条事件的 `seq`（或 `latest`）
   当作下一次的 `since`。
9. **无间隙信号**：若客户端的 `since` 早于环形缓冲里最老的一条（掉线太久 / 事件洪峰 / 期间发生
   `Clear()`），服务端**静默**只回现存部分，不报"丢失"。客户端只能靠 `latest` 与收到的最小
   `seq` 自行判断是否有缺口。
10. 服务端**不做去重、不做防抖**：同 type+text 的重复事件照发；防抖（安静满 1s、上限 6s）与
    去重是消费方（agent）的职责。
11. 多个客户端可以并发长轮询同一日志；事件是**广播**语义，读取不消费、不加锁独占。

## 7. 跨模块契约

### 7.1 本模块**依赖**（重实现时必须存在的下游接口）

- `MainThread.Run<T>(Func<T>, timeoutMs = 10000)`：把闭包编组到 Unity 主线程执行并同步等结果。
  超时抛 `TimeoutException`，消息逐字：
  `main-thread call not serviced within {timeoutMs}ms (game unfocused or scene loading?)`
  —— 该异常会冒泡到请求处理器，成为 **500** `{"error": "<该消息>"}`。
  HTTP 线程一律 `.GetAwaiter().GetResult()` 同步等待（不能用 async 端点，长轮询线程模型依赖阻塞）。
- `AgentBridgeMod`：`BuildSnapshot()`、`QueueFireMission(FireMissionRequest)`、
  `AdjustFireMission(AdjustFireRequest)`、`SetDeclaredTurret(kmX, kmY)`、
  `PrintOnTeleprinter(which, lines) → bool`、`PullSignalHorn() → string`。
  除 `PrintOnTeleprinter` 返回 bool 外，其余业务方法**返回人类可读字符串**，成功/失败都在文本里
  —— HTTP 层不解释语义（除了 `/fire` 的 `== "ok"` 判定）。
- `GameState.MapDrawer.Inspect() / Draw(placerIndex, prefabName, Vector2 origin, Vector2 target) / ClearAll()`
- `GameState.SceneFinder.Find(q)`
- `GameState.RequisitionOperator.InspectConsole() / StartPurchase(cardId, bearingDeg?, distanceKm?)`
- `GameState.ScoutPlaneOperator.Spawn(kmX, kmY, bearingDeg)`
- `Agent.AgentConfig.EnableHttpApi`（MelonPreferences 门控）。

### 7.2 本模块**暴露**给其他模块

- `EventLog.Append(type, source, text, data?)`：**所有**游戏侧生产者（地图轮询、电文轮询、
  弹着轮询、FCS 追踪、征用台、反炮击计时、号角、侦察机、任务阶段）都写这里。生产者一律在
  主线程调用；日志自身必须容忍任意线程。
- `EventLog.WaitForEvents / LatestSeq / Clear`：内置 agent 线程（`FdoAgent`）与 HTTP 长轮询
  **并列**消费同一日志，互不干扰。agent 用 `timeoutMs=0` 做工具执行期"随查搭车"。
- `EventLog.GameClock`（读写）：mod 更新循环写，事件追加与 agent 回执读。
- `Dtos.cs` 中的全部 DTO：既是 HTTP 响应契约，也被 agent 的快照文本渲染器直接消费 ——
  **改字段等于同时改两处协议**。
- `BridgeServer.Port` 常量对外可见（日志、诊断用）。

## 8. 不变量与防御性规则（必须单独遵守）

1. **主线程唯一性**：任何触碰 Unity / Il2Cpp 对象的代码只能在主线程泵里执行。HTTP 监听线程
   自身**绝不**直接读游戏对象，一律经 `MainThread.Run`。违反 = 随机崩溃。
2. **长轮询绝不进主线程**：`/events` 与 `/command` 必须在 HTTP 线程内完成，否则游戏暂停时
   事件通道整体失效。
3. **主线程不得被阻塞**：编组进去的闭包必须是短同步操作；物理长流程（购买、号角松手）走协程，
   结果异步经事件回来。
4. **Il2Cpp 读取全部包 try/catch**：反射/属性读随时可能抛（对象被销毁、类型被裁剪）。
   失败降级为默认值或跳过，绝不让单个字段读失败毁掉整个 `/state` 响应。
5. **响应写出异常必须吞掉**：客户端断开是常态。
6. **请求体解析异常必须吞掉**并转成 400，不得回 500。
7. **编码**：请求体固定 UTF-8 解码；响应用 `UnsafeRelaxedJsonEscaping` + UTF-8 字节写出。
   （外部陷阱：Windows curl 内联 `-d` 的中文会被转 GBK，必须 `--data-binary @utf8file`。）
8. **仅回环 + 默认关闭**：端点能开炮、花钱、画图，等价于本机 RCE 面；任何本地进程或对
   localhost 做 CSRF 的网页都能驱动游戏。不得加"方便起见"的默认开启或外网绑定。
9. **序号单调**：`Clear()` 之后序号继续增长，客户端旧游标不会"回到过去"。
10. **环形缓冲有界**：2048 条上限是内存保护，不得取消；溢出丢最老。
11. **显示串不可解析**：FCS 任务显示串只用于展示，机器可读信息只从 `serialToMarker` /
    `recentOutcomes` 结构化字段取。
12. **单位与坐标约定**（贯穿所有端点）：
    - 地图 local → km：`km = 原点偏移 + local × 3.8164`，原点偏移 **(10.016, 5.235)**。
    - `mapX/mapY` = local 单位；`kmX/kmY`、`ox/oy/tx/ty`、`offsetKm*`、`distanceKm`、
      `impactRadius`、`minKm/maxKm` = **km**。
    - `bearingDeg` = 度，**0 = 地图正北，顺时针增大**。
    - `motionSpeedKmh` = km/h；`motionAtTime`/`gameTime` = 24 小时制 `"HH:mm"`。
    - `validForSeconds`、`timeoutMs` 的时间单位分别是秒 / 毫秒。
    - 偏移上限 **±0.5 km**。
13. **正则**：本模块自身不含正则；相邻模块的两条协议性正则须一并保留 ——
    任务流水号 `^#(\d+)\b`（`AgentBridgeMod.cs:424-425`，Compiled）与
    富文本剥离 `<[^>]{1,64}?>`（`GameState/TeleprinterReader.cs:15`，Compiled）。

## 9. 逐字保留数据块

本模块三个源文件中**没有**长篇自然语言数据块（SystemPrompt / MapIntelTable / 学说文本均不在此）。
以下短文本已在上文逐字给出，重实现时按原样搬运：404 端点清单、各 400 错误文案、
`/command` 的中文错误与成功回执、`EnableHttpApi` 的 description、三条启动/失败日志。

与本模块协议直接相邻、但**归属其他模块**的长中文文本（不在此复制，重实现时从原文件搬运）：

- `AgentBridgeMod.cs:886-908` —— 平民保护/友军误伤拒绝与覆盖名单后缀模板。
- `AgentBridgeMod.cs:797-798` —— 盲射警告模板。
- `AgentBridgeMod.cs:449-450`、`458-459` —— 任务失败 / 炮弹出膛事件模板。
- `AgentBridgeMod.cs:160-161` —— 弹着推定销账事件模板。
- `AgentBridgeMod.cs:203-231` —— 反炮击倒计时四态事件文本。
- `GameState/ImpactReader.cs:106-141` —— 弹着确认 / 黄箭头修正提示模板（含"（不准确）"措辞，
  刻意只转述玩家可见精度，不得泄露误差参数）。
- `AgentBridgeMod.cs:510-512` —— 误伤预警事件模板。
- `AgentBridgeMod.cs:667-671`、`726`、`742-747`、`752` —— fire/turret 的拒绝文案。

## 10. 未决问题（需人裁决）

1. **`POST /fire` 成功也回 409**：状态码判定为 `result == "ok"`，而成功返回值是 `ok (#N){suffix}`，
   永不相等。是"客户端一律看 result 文本、状态码无意义"的既成事实，还是应修成前缀判定？
2. **404 的 `endpoints` 清单只有 4 条**，与实际 14 个端点严重不符。要补全、还是保留成"最小提示"？
3. **`FireMissionRequest.markerId`（默认 4）疑似死字段**：标记体制重构后入队走纯坐标路径，
   桥不再移动任何地图标记。是留作兼容占位，还是删除？
4. **`/adjust` 的 400 文案说 `target`，实际字段是 `targetPoint`**（`/fire` 的注释同样混用
   "target" 与 `targetPoint`）。是否应把外部字段名统一改成 `target`（并保留 `targetPoint` 别名）？
5. **`/requisition` 无法传 `distanceKm`**：第三参写死 null，而 `MoveDirection` 卡需要距离拨盘；
   HTTP 路径也没有 `priority`/`startGrid`，能力弱于 agent 工具 `requisition_card`。是有意的
   "调试端点只做最小子集"，还是遗漏？
6. **业务失败的状态码不统一**：`/fire`、`/print` 用 409，`/turret`、`/adjust`、`/horn`、
   `/draw`、`/requisition`、`/scoutplane` 的失败一律 200（错误藏在 `result`/`error` 文本里）。
   重实现要不要统一？统一会破坏现有客户端。
7. **响应包装不统一**：`/markers`、`/find`、`/console`、`/scoutplane`、`/state` 回原始对象，
   其余回 `{"result": …}`。`/console` 顶层还是数组。是否统一包一层？
8. **`/draw` 的 placerIndex 越界处理自相矛盾**：越界时实际使用 `placers[0]`，但写进
   `MapMarkerSaveData.PlacerIndex` 的仍是请求里的越界值。应当拒绝、还是把回落后的索引写回？
9. **长轮询缺口不可观测**：客户端无法区分"这段时间没事件"与"事件被 2048 上限或 `Clear()` 冲掉了"。
   是否需要在响应里加 `oldest`（或 `cleared` 世代号）字段？
10. **`since` 缺省取 `LatestSeq` 而非 0**：新客户端首次请求会静默丢掉全部历史。是刻意（避免
    重放）还是应显式要求客户端传 `since=0` 才回历史？
11. **无认证 / 无 Origin 校验**：默认关闭是唯一防线。是否需要一个共享 token 或
    `Origin`/`Sec-Fetch-Site` 拒绝，以挡住本地网页的 CSRF？
12. **`/state` 与 `/events` 之间无一致性保证**：快照与事件流各自独立，客户端拿到的快照可能比
    刚收到的事件旧。是否需要在快照里带上 `latestSeq` 做对齐？
