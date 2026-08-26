# 模块 fcs-gw —— FCS 反射网关需求

来源文件：`C:/Users/stevenli/Codes/IronNestAgentBridge/Fcs/FcsGateway.cs`（全文 448 行）
相关 DTO：`C:/Users/stevenli/Codes/IronNestAgentBridge/Dtos.cs:56-77`（`FcsStatusDto`）

本模块是桥（IronNestAgentBridge）与火控 mod **IronNestFCS Smart** 之间**唯一**的对接面。
它必须**完全通过反射**工作：桥的程序集不得引用 FCS Logic 程序集，不得出现任何 FCS 的强类型
引用，也不得在任何跨重载生存期内缓存类型句柄、`MethodInfo`/`FieldInfo` 或实例。

---

## 1. 解析链与生命周期（核心不变量）

### 1.1 反射链（必须逐字使用这些名字）

```
MelonMod.RegisteredMelons 中 Info.Name == "IronNestFCS Smart" 的 melon   （宿主 FcsHostMod）
  → 私有实例字段  _reloader        (LogicReloader)
  → 公有实例属性  Current          (IFcsModule / FcsModule)
  → 私有实例字段  _fcs             (FSC)
```

- 宿主 melon 名常量：`"IronNestFCS Smart"`。匹配规则是 `melon.Info != null && melon.Info.Name == FcsModName`
  的**精确相等**（非子串、非忽略大小写），取第一个命中者。
- 所有成员查找一律使用绑定标志 `BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic`
  （下称 `AnyInstance`）——除**少数几处刻意只查 public 的写入点**，见 §5.4。
- 解析必须返回两个诊断布尔：
  - `modPresent`：找到了名为 `IronNestFCS Smart` 的 melon（哪怕 Logic 未加载）。
  - `logicLoaded`：`Current` 属性返回了非 null 的 module 实例。
- 解析失败的四个分支及其副作用：
  1. 没找到宿主 melon → `modPresent=false`、`logicLoaded=false`、返回 null（**不动缓存**）。
  2. `_reloader` 为 null → `modPresent=true`、`logicLoaded=false`、返回 null（**不动缓存**）。
  3. `Current` 为 null → `modPresent=true`、`logicLoaded=false`，**必须把缓存的 module 与 FSC 双双清空**，返回 null。
  4. `_fcs` 取不到 → 返回 null，但 `logicLoaded` 已为 true。

### 1.2 ALC 重载不变量（这是本模块存在的理由）

- FCS Logic 程序集活在**可回收的 AssemblyLoadContext** 里，每次 **F9 / 场景加载**都会整体销毁重建。
- 因此：**绝不可**跨重载持有强引用或缓存。允许的唯一缓存是「上一次见到的 module 实例引用 + 由它取出的
  FSC 实例」，并且必须用 **引用相等（`ReferenceEquals`）判定 module 身份**：
  - 若 module 身份变化 **或** 已缓存的 FSC 为 null → 重新从 module 取 `_fcs` 并覆盖两个缓存；
  - 否则复用缓存的 FSC。
- 每个对外方法都必须**在调用当次重新走一遍解析链**，不得把上一帧解析结果当作长期句柄。
- 特别地：FCS 的协程锁（§4.1）**每次调用现取现用，禁止跨 F9 缓存**。

---

## 2. 状态读取：`ReadStatus()` → `FcsStatusDto`

### 2.1 契约

- 无论解析是否成功，都必须返回一个非 null 的 DTO，并已填好 `ModPresent` / `LogicLoaded`。
- FSC 不可用时，其余字段保持默认值（false / 0 / null / 空集合）并**立即返回**。
- 读取属性时的取值器必须**逐个 try/catch**：任一属性抛异常只让该字段回落到默认值，不得让整次读状态失败。

### 2.2 从 FSC 读取的属性 → DTO 字段映射（属性名与字段名均逐字）

| FSC 属性（`AnyInstance`） | 类型 | → DTO 字段 |
|---|---|---|
| `IsBound` | bool | `Bound` |
| `PendingCount` | int | `PendingCount` |
| `AutoFireEnabled` | bool | `AutoFireEnabled` |
| `MaxChargeEnabled` | bool | `MaxChargeEnabled` |
| `CompletedTaskCount` | int | `CompletedTaskCount` |
| `SuccessfulTaskCount` | int | `SuccessfulTaskCount` |
| `FailedTaskCount` | int | `FailedTaskCount` |
| `LeftTask` | ArtilleryTask | `LeftTask`（经 §2.3 渲染成字符串） |
| `RightTask` | ArtilleryTask | `RightTask`（同上） |
| `QueueCan` | IEnumerable | 逐项渲染后按序追加进 `PendingTasks` |
| `RecentTasks` | IEnumerable | 折算成 `RecentOutcomes`（§2.5） |

- `LeftTask` / `RightTask` / `QueueCan` 中的**每一个**任务对象都必须再喂给序号映射登记（§2.4）。
- 遍历 `QueueCan` 与遍历 `RecentTasks` 各自整体包 try/catch（枚举中途抛错就放弃该集合的剩余项，
  但不影响已填好的其它字段）。

### 2.3 任务显示串（`DescribeTask`）——格式必须逐字保持

- 输入 null → 输出 null。
- 运动后缀：反射调用任务上的实例方法 `MotionSuffix`，**单参数 `true`**，结果按 string 取；
  失败或不存在时后缀为空串 `""`。（stock FCS 无此方法。）
- 头部：读字段 `serial`；`serial > 0` 时头部为 `#{serial}`，否则（stock FCS 无 serial 字段）
  回退为 `T{targetId}`。
- 完整格式（字段名与格式说明符逐字）：

  ```
  {head} {bulletType} brg {angel:F1} dist {distance:F2}km chg {chargeCount} [{progress}]{motionSuffix}
  ```

  当 `failureReason` **不等于空串**时，再追加：

  ```
   fail: {failureReason}
  ```

  （前导一个空格；判定用的是「与 `""` 相等则不追加」，故 null 也会被追加成 ` fail: `——见开放问题。）
- 单位约定：`angel` 是**方位角（度）**保留 1 位小数；`distance` 是**公里**保留 2 位小数并带 `km` 后缀；
  `chargeCount` 是装药数（整数）。
- 整个渲染过程包 try/catch；一旦抛错，回退为 `task.ToString()`。

### 2.4 序号→标记映射（`SerialToMarker`）

- 目的（硬性设计约束）：桥**永远不许正则解析显示串**来取任务号或标记号；一切寻址走这张结构化表。
- 登记规则：从任务对象读字段 `serial`（int）与字段 `targetId`（int）；当 `serial > 0` 且 `targetId`
  成功拆箱时，写入 `SerialToMarker[serial] = targetId`。
- 整个登记包 try/catch，静默跳过异常项。
- 覆盖范围：左炮任务、右炮任务、队列全部任务（即「当前活着的任务集合」）。这张表的 **Keys 就是
  『还未出膛的 serial 集合』**，桥的出膛判定依赖它（见 §6 跨模块契约）。

### 2.5 近期结果（`RecentOutcomes`）

- 遍历 FSC 属性 `RecentTasks`；每项：
  - 读字段 `serial`；不是 int 或 `<= 0` → 跳过。
  - 读字段 `progress`，`ToString()`，取不到时为 `""`。
  - 读字段 `failureReason` 按 string 取，取不到时为 `""`。
  - 写入：`progress == "Failed"` 时值为 `$"Failed: {reason}"`，否则就是 `progress` 原文
    （典型值如 `Finished`）。字符串 `"Failed"` 与前缀 `"Failed: "` 是**协议字面量**，
    下游按它区分「失败」与「完成」。
- 每项单独 try/catch。

---

## 3. 任务入队

三条入队路径共享的前置与错误协议：

- 先解析 FSC；失败时按**优先级顺序**返回诊断串（逐字，注意三条入队路径与 `AdjustTaskAim` 共用同一组）：
  1. `!modPresent` → `"IronNestFCS Smart mod not present"`
  2. `!logicLoaded` → `"FCS Logic not loaded (scene not bound yet?)"`
  3. 其它 → `"FCS instance unavailable"`
- 成功一律返回 `"ok"`（下游按 `== "ok"` 判成功）。
- 入队方法：FSC 实例方法 `EnqueueTask`，单参数为构造好的 `ArtilleryTask`。
  找不到该方法时返回 `"FSC.EnqueueTask not found"`。

### 3.1 Logic 内部类型（全限定名逐字）

- 任务类型：`IronNestFCS.Logic.FCS.ArtilleryTask`
- 弹种枚举：`IronNestFCS.Logic.FCS.BulletType`
- **必须从「当前 FSC 实例的类型所在的程序集」取这两个类型**（即 `fsc.GetType().Assembly`），
  这样才能落在当前存活的 ALC 里。
- 任一类型取不到 → 返回 `"FCS internal types not found (incompatible FCS version?)"`。
- 弹种解析：`Enum.Parse(BulletType, shell, ignoreCase: true)`（**大小写不敏感**）。
  失败 → 返回 `$"unknown shell type '{shell}'"`（单引号包裹原样输入）。

### 3.2 `EnqueueAimPoint`（主用路径：纯坐标晚绑定任务）

签名语义：`EnqueueAimPoint(localX, localY, bearingDeg, distanceKm, shell, priority, out serial,
trackEntityId = null, motion = null, validForSeconds = null) → 结果串`

- `serial` 输出参数**先置 -1**；任何失败路径都保持 -1。
- 能力探测：`ArtilleryTask` 上若**没有** public 字段 `hasAimPoint`，直接返回
  `"FCS build lacks aim-point tasks — update the FCS fork"`（注意其中的 **em dash `—`**，
  UTF-8 字面量，见 §7 编码陷阱）。
- 构造任务并写入字段（字段名逐字）：
  - `targetId` = **0**（明确表示「不绑定任何地图标记」）
  - `angel` = `bearingDeg`（度）
  - `distance` = `distanceKm`（公里）
  - `position` = `Vector3(10.016f + localX * 3.8164f, 5.235f + localY * 3.8164f, 0f)`
    —— 即把「Draggable Surface 局部坐标」换算成 **km 帧**：`km = 局部 × 3.8164`，
    km 帧原点偏移 `(10.016, 5.235)`。这三个数是坐标协议常量，必须逐字保留。
  - `bulletType` = 解析出的枚举值
  - `hasAimPoint` = `true`
  - `aimLocal` = `Vector3(localX, localY, 0f)`（**局部帧**，不做换算）
- 可选写入：优先级（§3.5）、运动模型/跟踪目标（§3.4）。
- 时效：仅当 `validForSeconds` 有值且 **> 0** 时，写入字段 `validForSeconds`（try/catch 静默）。
  值 ≤ 0 或 null 视为「无时效」。
- 入队后**从任务对象读回 public 字段 `serial`** 作为 `out serial`（读不到 → -1）。
  这是「FCS 在入队时分配唯一流水号」的取回点，是桥后续 adjust/cancel/簿记的唯一句柄。
  读回包 try/catch。
- 晚绑定语义（行为要求）：该任务不依赖任何物理标记，FCS 每个规划轮会用 `aimLocal` 重新解算，
  玩家手里的 T1–T8 标记不会被桥移动。

### 3.3 `EnqueueByBearing` 与 `EnqueueFromMarker`

**`EnqueueByBearing(bearingDeg, distanceKm, shell, targetId, priority = 50)`**

- 构造 `ArtilleryTask` 并写：`targetId`（调用方给的标记号）、`angel`、`distance`、
  `position = Vector3.zero`、`bulletType`，再尝试写 `priority`。
- 不设 `hasAimPoint`/`aimLocal`/运动模型/时效。
- 入队后**不回读 serial**。

**`EnqueueFromMarker(markerId, shell, priority = 50, trackEntityId = null, motion = null)`**

- 取 FSC 字段 `MapTable`（`AnyInstance`）；null → `"FSC.MapTable unavailable"`。
- 取 `MapTable` 实例方法 `GetMarkTarget`；null → `"MapTable.GetMarkTarget not found"`。
- 调用 `GetMarkTarget(markerId)` 得到任务对象；null → `$"marker {markerId} not resolvable on map"`。
  **设计意图**：让方位/距离/网格来自与「人类点一下标记」**完全相同**的代码路径。
- `BulletType` 从**该任务对象类型所在的程序集**取（不是从 FSC 类型取，等价但来源不同）。
- 写 `targetId = markerId`、`bulletType`，尝试写 `priority`、运动模型。
- 入队（此路径不做 `EnqueueTask` 存在性检查）。返回 `"ok"`。
- 现状：本方法**保留但未被调用**（标记体制重构后桥已彻底不移动地图标记）。见开放问题。

### 3.4 运动模型注入（`MotionSpec` / `TrySetMotion`）

- `MotionSpec` 是本模块对外暴露的不可变记录，字段顺序与语义：
  `MotionSpec(OriginLocalX, OriginLocalY, VelLocalX, VelLocalY, T0Seconds)`
  —— **map-local 帧**（不是 km 帧），速度单位 **局部单位/秒**，`T0Seconds` 是**任务时钟（世界任务时钟）秒**。
  线性模型：`p(t) = origin + vel · (t − t0)`。
- 写入的任务字段（仅存在于打过补丁的 FCS fork；stock FCS 上静默忽略）：
  - `trackEntityId`（string）：仅当传入的 id **非 null 且非空串**时写入。
  - `hasMotion` = `true`
  - `motionOriginLocal` = `Vector3(OriginLocalX, OriginLocalY, 0f)`
  - `motionVelLocalPerSec` = `Vector3(VelLocalX, VelLocalY, 0f)`
  - `motionT0` = `T0Seconds`
  - 后四项**仅当 `motion != null` 时**整体写入。
- 整块写入包一个 try/catch，任何失败静默吞掉（保证 stock FCS 上仍能正常入队）。

### 3.5 优先级注入（`TrySetPriority`）

- 写任务字段 `priority`（**只查 public 实例字段，不用 `AnyInstance`**），try/catch 静默。
- 该字段只存在于打过补丁的 FCS；stock FCS 直接忽略优先级。
- 默认值 `50`（各入队方法的默认实参）。语义常量在别处：P≥90 跳过凑单窗、P100 抢占等。

---

## 4. 其它 FSC 能力探测与调用

### 4.1 `GetRequisitionLock()` —— 征用台共享控制台锁

- 取 FSC 的 `SharedResources`：**先试属性、属性没有再试字段**（都用 `AnyInstance`）。
- 再从其上取属性 `Requisition`（FCS Logic ALC 中的 `CoroutineLock`）。
- 任一环节缺失 → 返回 null；**FCS 未加载时返回 null，调用方据此「不加锁直接执行」**。
- 每次调用现取，禁止缓存（见 §1.2）。

### 4.2 `RequestCardPurchase(cardId, bearingDeg?, priority = 50, startGrid?, distanceKm?)`

- 目的：把打孔卡购买请求作为 DTO 提交给 FCS 的控制台协调器（打过补丁的 FCS），桥自己不再持锁。
- **按下列顺序**探测 FSC 实例方法 `RequestConsoleCard` 的重载，命中第一个就用（形参类型序列逐字）：

  | # | 形参类型序列 | 实参 |
  |---|---|---|
  | 1 | `(string, float, bool, float, bool, int, string)` | `cardId, bearingDeg ?? 0f, bearingDeg.HasValue, distanceKm ?? 0f, distanceKm.HasValue, priority, startGrid` |
  | 2 | `(string, float, bool, int, string)` | `cardId, bearingDeg ?? 0f, bearingDeg.HasValue, priority, startGrid` |
  | 3 | `(string, float, bool, int)` | `cardId, bearingDeg ?? 0f, bearingDeg.HasValue, priority` |
  | 4 | `(string, float, bool)` | `cardId, bearingDeg ?? 0f, bearingDeg.HasValue` |

- 「有没有方位/距离」用**成对的 (值, 是否有值) 布尔**表达，缺省值一律 `0f`。
- 返回值按 string 取（FCS 侧的受理串）。
- **FSC 不可用、或四种重载全无 → 返回 null**，调用方据此回退到桥自己的「物理模拟购买」老路径。

### 4.3 `ReadConsoleCardResult()`

- 读 FSC 属性 `ConsoleCardRequestResult`（string），供轮询「最近一次控制台卡请求结果」。
- FSC 不可用 → null。无任何异常处理包装以外的加工。

### 4.4 `AdjustTaskAim(serial, localX, localY)` —— 最后时刻改瞄

- 语义：把**已在队列中或已上炮准备中**的任务改瞄到新的 **map-local 点**。
  **非阻塞**：FCS 从不等待改瞄，其分段重解流水线会在下一个时机吸收新瞄点；改不上就按原瞄点发射。
- 解析失败诊断串同 §3（三条，逐字）。
- FSC 无实例方法 `AdjustTaskAim` → 返回 `"FCS build lacks AdjustTaskAim"`。
- 调用 `AdjustTaskAim(serial, localX, localY)`，返回其 string；**返回 null 时替换为 `"adjust failed"`**。
- 坐标是**局部帧**（与 `aimLocal` 同帧，不做 km 换算）。

### 4.5 `TryGetTaskInfo(serial, out shell, out markerId)`

- 输出初值：`shell = null`、`markerId = -1`。
- FSC 不可用 → 返回 false。
- 查找顺序：`LeftTask` → `RightTask` → 遍历 `QueueCan`（属性名逐字，`AnyInstance`），
  取**第一个** `serial` 字段严格相等的任务。
- 命中时输出：`shell` = 字段 `bulletType` 的 `ToString()`（可能为 null）；
  `markerId` = 字段 `targetId` 拆箱为 int，拆不出时 **-1**。返回 true。
- 全程 try/catch，任何异常 → 返回 false。
- 用途：把 `#N` 反解成「弹种 + 内部标记号」，供桥侧簿记与提示，仍**不依赖显示串解析**。

### 4.6 `CancelPending(serial)` —— 撤销尚未执行的任务

- 解析失败诊断串与入队路径**不同**（更短的一组，必须逐字保留这种差异）：
  1. `!modPresent` → `"FCS mod not present"`
  2. `!logicLoaded` → `"FCS logic not loaded"`
  3. 其它 → `"FCS unavailable"`
- FSC 无实例方法 `CancelPendingTask` → 返回 `"FCS build lacks CancelPendingTask"`。
- 调用 `CancelPendingTask(serial)` 取 string 结果：
  - 结果为 null → 返回 `$"no pending task with #{serial}"`
  - 否则 → 返回 `$"cancelled: {cancelled}"`
- 只针对**尚未开始执行**的任务；已上炮任务不由此撤销。

---

## 5. 不变量与防御性规则（single out）

1. **主线程 only**。所有入队、改瞄、撤销、状态读取都必须在 Unity 主线程上发生
   （桥侧经 `MainThread.Run` 同步调用）。后台 agent 线程绝不可直接调本模块。
2. **禁止跨 ALC 缓存**。除 §1.1 描述的「module 身份 + FSC 实例」这一对短命缓存外，
   不得缓存任何 `Type` / `MethodInfo` / `FieldInfo` / 枚举值 / 锁对象。F9 后一切重解析。
3. **Il2Cpp / 反射防御**：每一个反射读取点都必须能吞掉异常并回落到默认值；
   本模块**任何路径都不得向调用方抛异常**（唯一的例外是 `Activator.CreateInstance` 与
   核心必需字段的 `!` 断言路径——那里一旦失败说明 FCS 版本不兼容，属于「宁可炸也别静默错」）。
4. **能力探测而非版本判断**：区分 stock FCS 与本项目 fork 一律靠「字段/方法是否存在」
   （`hasAimPoint`、`serial`、`priority`、`AdjustTaskAim`、`CancelPendingTask`、
   `RequestConsoleCard` 各重载、`MotionSuffix`、运动模型字段），不得读版本号。
5. **绝不正则解析显示串**。序号、标记号、结果一律从结构化字段/映射读取。
   显示串只面向人（HUD、面板、快照文本）。
6. **T 编号已废除对外寻址**。对外句柄只有唯一流水号 `#N`（`serial`）；`targetId`（T 号）是
   会被回收复用的内部标记号，只在 `SerialToMarker` 与 `TryGetTaskInfo` 里作为内部量出现。
   仅 stock FCS（无 `serial` 字段）时 `DescribeTask` 才回退到 `T{targetId}` 前缀。
7. **写入-静默 vs 写入-必须**：`priority` / `trackEntityId` / 运动模型 / `validForSeconds`
   属于「有则写、无则算」（try/catch 静默）；`targetId` / `angel` / `distance` / `position` /
   `bulletType` / `hasAimPoint` / `aimLocal` 属于「必须存在」（缺失即视为 FCS 不兼容）。
8. **编码陷阱**：本模块的诊断串中含中文语境之外的 **em dash `—`**
   （`"FCS build lacks aim-point tasks — update the FCS fork"`）。源文件必须以 UTF-8 保存，
   且**绝不可**用中文 Windows 的 PowerShell `Get-Content`/`-replace`/`Set-Content` 改写
   （会按 GBK 误读 UTF-8 再回写导致乱码）。
9. **热重载事故预防**：FCS Logic 在游戏运行时落盘会当场重置在用 FCS（队列/校准/任务全丢）。
   本模块的能力探测行为必须容忍「Logic 刚被换掉、FSC 实例已易主」这一情况——
   靠 §1.1 的 module 身份比对自动恢复，不得要求重启桥。
10. **本模块不写日志**。所有失败以**返回值字符串**上报调用方，模块内部不打印任何 MelonLogger 行。

---

## 6. 跨模块契约

### 6.1 本模块**暴露**给其它模块的接口

| 成员 | 用途 / 消费方 |
|---|---|
| `ReadStatus() → FcsStatusDto` | 状态快照模块、HTTP `GET /state`、F10 面板、agent 快照文本 |
| `FcsStatusDto.SerialToMarker`（Keys） | **出膛判定**：桥簿记里存在、而该集合中已消失的 serial ⇒ 炮弹已出膛（`shell_fired` 事件）；不做任何物理归位 |
| `FcsStatusDto.RecentOutcomes` | 区分「出膛」与「任务失败（未发射）」；`Failed: {reason}` 前缀是判据；时效过期任务经此上报 agent |
| `FcsStatusDto.PendingTasks` / `LeftTask` / `RightTask` | 队列纪律三态之二（第三态是桥自持的在途炮弹清单） |
| `EnqueueAimPoint(..., out serial)` | `fire` 工具 / `POST /fire` 的唯一入队路径；返回的 `serial` 是桥 `_deployedTasks` 簿记键 |
| `AdjustTaskAim(serial, localX, localY)` | `adjust_fire` 工具 / `POST /adjust` |
| `CancelPending(serial)` | `cancel_pending_task` 工具 |
| `TryGetTaskInfo(serial, out shell, out markerId)` | 弹着匹配、事件文案、改瞄前的任务校验 |
| `RequestCardPurchase(...)` / `ReadConsoleCardResult()` | `requisition_card` 工具 / `POST /requisition`；返回 null ⇒ 回退桥侧物理购买 |
| `GetRequisitionLock()` | 桥侧物理操作征用台时的互斥；返回 null ⇒ 不加锁直行 |
| `MotionSpec` 记录类型 | agent 的 `motionFrom` 转录 → 入队时注入运动模型 |
| `EnqueueByBearing(...)` / `EnqueueFromMarker(...)` | 目前无调用方（见开放问题） |

### 6.2 本模块**依赖**的外部契约

- **MelonLoader**：`MelonMod.RegisteredMelons`、`MelonMod.Info.Name`。
- **UnityEngine**：`Vector3`（构造 `position` / `aimLocal` / 运动模型向量）。
- **IronNestFCS Smart（fork）**：§1.1 反射链 + §2/§3/§4 中列出的全部类型与成员名。
  这是一份**隐式 ABI**：FCS 侧任何一处改名都会让对应能力静默降级为诊断串。
- **坐标系约定（与桥的坐标模块共享）**：`Draggable Surface` 局部单位 × `3.8164` = km，
  km 帧原点偏移 `(10.016, 5.235)`；方位角单位=度，距离单位=km，时间单位=秒（任务/世界时钟）。
- **调用方责任**：主线程调度、`FcsRuntimeClock.IsFocused` 门槛、agent 侧的友军普查与预算门
  ——本模块**一律不做**这些校验，给什么打什么。

---

## 7. 逐字保留数据块

无。本文件中不含大段自然语言数据（无 SystemPrompt 段落、无 MapIntelTable 情报、无学说文本）。
所有需要逐字保留的内容都是短标识符/消息串/数值常量，已在正文中内联给出。

---

## 8. 错误消息总表（逐字，供实现对照）

```
IronNestFCS Smart mod not present
FCS Logic not loaded (scene not bound yet?)
FCS instance unavailable
FCS mod not present
FCS logic not loaded
FCS unavailable
FCS internal types not found (incompatible FCS version?)
FCS build lacks aim-point tasks — update the FCS fork
FCS build lacks AdjustTaskAim
FCS build lacks CancelPendingTask
FSC.EnqueueTask not found
FSC.MapTable unavailable
MapTable.GetMarkTarget not found
unknown shell type '{shell}'
marker {markerId} not resolvable on map
adjust failed
no pending task with #{serial}
cancelled: {cancelled}
ok
```

## 9. 数值/字面常量总表

| 常量 | 值 | 单位/含义 |
|---|---|---|
| 宿主 melon 名 | `IronNestFCS Smart` | 精确匹配 |
| 局部→km 比例 | `3.8164` | km / 局部单位 |
| km 帧原点 X | `10.016` | km |
| km 帧原点 Y | `5.235` | km |
| 默认 priority | `50` | 0–100，越大越优先 |
| 失败进度字面量 | `Failed` | `progress.ToString()` 比对值 |
| `MotionSuffix` 实参 | `true` | bool |
| 无效 serial / markerId | `-1` | 哨兵值 |
| 纯瞄点任务的 `targetId` | `0` | 表示不绑定标记 |
| `validForSeconds` 生效门槛 | `> 0` | 秒 |
