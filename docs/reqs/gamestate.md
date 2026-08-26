# 模块需求：gamestate（游戏状态读写层）

本节描述 mod 与游戏世界之间的唯一接触面：一组 reader（只读战场信息）与 operator（物理操作道具）。
上层（agent / HTTP / FCS 网关）只通过本层的 DTO、事件与返回字符串看世界，本层之外不得直接触碰
Il2Cpp 对象。

---

## 0. 全模块共同约定（先于任何单个 reader）

### 0.1 坐标与单位

- **地图局部坐标（map-local）**：`Draggable Surface` 这个 Transform 的 local 空间，x/y 为平面，z 忽略。
- **km 帧**：对外（LLM、HTTP、日志、事件）唯一使用的坐标系。换算常量必须逐字保留：

  | 常量 | 值 | 含义 |
  |---|---|---|
  | `MapLocalToKm` | `3.8164f` | map-local 单位 → km 的比例 |
  | `MapOffsetX` | `10.016f` | km 帧原点在 X 上的偏移 |
  | `MapOffsetY` | `5.235f` | km 帧原点在 Y 上的偏移 |

  正向：`kmX = 10.016f + local.x * 3.8164f`，`kmY = 5.235f + local.y * 3.8164f`
  反向：`local.x = (kmX - 10.016f) / 3.8164f`，`local.y = (kmY - 5.235f) / 3.8164f`

  旧实现里这三个常量在 `MapReader`、`ImpactReader`、`ScoutPlaneOperator`、`SceneFinder`、
  `AgentBridgeMod` 各自硬编码重复了一遍。**重实现必须收敛到单一来源**，但数值一字不改。

- **方位（bearing）**：`0° = 地图正北（map-local +Y）`，**顺时针**增大，规范化到 `[0, 360)`。
  - 由 delta 求方位：`Vector3.SignedAngle(delta, Vector3.up, Vector3.forward)`，负值 `+360f`；
    或等价的 `Atan2(dx, dy) * Rad2Deg` 后 `(v % 360 + 360) % 360`。两处旧代码都存在，结果必须一致。
  - 由方位求点：`x = origin.x + sin(rad) * r`，`y = origin.y + cos(rad) * r`。
- **距离**：`|delta_local|（z 清零）× 3.8164f` = km。
- **`ShellDefinition.ImpactRadius` 的单位是 km，不是米**（HE=0.25、HCHE=0.55、AP=0.15）。
  对外显示米时必须 `× 1000f`。曾按米处理导致"爆半径 0m"、友军拦截失效。

### 0.2 线程与异常

- 本模块所有方法**只能在 Unity 主线程执行**。HTTP 线程与 agent 线程必须经 `MainThread.Run`
  （同步等结果，默认超时 10000ms）或 `MainThread.Post`（fire-and-forget，仅用于装饰性工作如画图）。
- 每一次 Il2Cpp 属性/字段读取**必须单独 try/catch**，失败时保留该字段默认值继续，
  绝不允许单个字段读失败毁掉整个 DTO 或中断遍历。这是本模块最普遍的防御模式。
- 任何 `GetComponentsInChildren` / `FindObjectsOfType` 调用都要包在 try/catch 里，失败返回空结果而非抛出。

### 0.3 战争迷雾铁律

- 任何**暴露给 LLM** 的实体列表只能包含 `Visible == true` 的实体。
- 只有内部 diff（用于判断"新出现"）才允许读取隐藏实体，且该结果**不得**外流到快照/事件/工具回执。
- 弹着修正提示只转述玩家屏幕上能看到的模糊度，不得泄露底层真值（见 §4.2）。

---

## 1. MapReader —— 指挥桌（战术地图）

有状态实例（mod 持有一份），生命周期跟随场景。

### 1.1 绑定（TryBind）

按名查找三个对象：

| 对象名 | 用途 | 缺失后果 |
|---|---|---|
| `TurretLocation` | 真锚点（权威物理位置，**永不移动**） | 必需，缺失则绑定失败 |
| `Draggable Surface` | 地图台面，map-local 空间的根 | 必需，缺失则绑定失败 |
| `Fire Mission Root` | 目标实体的父节点 | 可缺（只是读不到实体） |

**同名陷阱**：`GameObject.Find("TurretLocation")` 抓到的是真锚点；`Canvas/MapRoot/TurretLocation`
是静态图标；可拖动的推断真源是 `Draggable Surface/Player Turret Piece`。三者不可混用。

绑定时同时扫描玩家标记：遍历 `Draggable Surface` 的**直接子物体**，名为 `MapToken_Artillery` 的，
用 `GetComponentInChildren<TextMeshPro>()` 的 `.text` 解析 int 作为编号，登记 transform 与
"home"（初始 localPosition）。

绑定成功后测量图幅（§1.2），置 `IsBound = true`。

`Unbind()` 必须清空：`IsBound=false`、`KmBounds=null`、三个 Transform 引用、标记表、home 表、
上一帧实体快照。

绑定重试节律由上层驱动：`BindRetrySeconds = 2f`；`OnSceneWasLoaded` 时 `Unbind` 并重排重试。

### 1.2 图幅测量（KmBounds）

- 目的：得到**本关真实**的射击包络。硬编码的 A..Z 包络曾让盲射飞出小图边缘数公里。
- 算法：遍历 `Draggable Surface` 下所有 `Renderer`，取其 world AABB 的 8 个角点，
  `InverseTransformPoint` 回 surface-local，取 x/y 的 min/max；选**面积最大**的那个 renderer 作为图纸。
- 转 km 后做合理性门：`width ∈ [5, 40]` 且 `height ∈ [3, 30]`（单位 km），否则**丢弃测量结果**
  （返回 null，上层回退宽松包络），并输出警告，格式逐字：

  ```
  [AgentBridge] map sheet measurement implausible ({width:F1}x{height:F1}km via '{sheet.gameObject.name}') — keeping generous bounds
  ```

- 测量成功时上层日志（属于 mod 层，但同一契约）：
  `[AgentBridge] tactical map bound; sheet extent km({MinX:F1},{MinY:F1})-({MaxX:F1},{MaxY:F1})`
  失败时：`[AgentBridge] tactical map bound; sheet unmeasured — generous bounds fallback`
- 结果通过 `GridMath.SetMapBoundsKm(minX, minY, maxX, maxY)` 生效；未测量时 `GridMath.ResetMapBounds()`。

### 1.3 炮位原点（TurretLocalOnMap）

优先级顺序，不可颠倒：

1. `Draggable Surface/Player Turret Piece` 的 `localPosition`（"指挥部认为炮塔在哪"的推断真源，
   FCS 与桥的射击原点；摆错就打偏，by design）。查找结果可缓存。
2. 回退：`Draggable Surface.InverseTransformPoint(TurretLocation.position)`。
3. 未绑定：`Vector3.zero`。

`SetDeclaredTurret(kmX, kmY)`：只移动棋子，**绝不动真锚点**。只改 x/y，保留原 z。
返回字符串三选一（逐字）：

- `map not bound`
- `'Player Turret Piece' not found on the map`（其中 `Player Turret Piece` 来自常量 `PlayerTurretPieceName`）
- `turret piece moved to km({kmX:F2},{kmY:F2}); solutions now use it as origin`

上层据"返回串不含 `not` 且不含 `rejected`"判定成功——重实现时请保留这三个串的措辞，或改为
结构化成功标志（更好，但要同步改上层）。

### 1.4 解算辅助

- `Solution(entityLocal, turretLocal)` → `(bearingDeg, distanceKm)`，规则见 §0.1。
- `SolutionToMapLocal(bearingDeg, distanceKm)` → map-local 点：`r = distanceKm / 3.8164f`，
  `x = turret.x + sin(rad)*r`，`y = turret.y + cos(rad)*r`，z=0。

### 1.5 标记读取

`ReadMarkers()` → `MarkerDto { Id, MapX, MapY, BearingDeg, DistanceKm }`，未绑定返回空表，
transform 已销毁的条目跳过。MapX/MapY 是 **map-local**，不是 km。

`TryMoveMarker(id, mapX, mapY)` / `ReturnMarkerHome(id)` / `MarkerIds` / home 表：
在"桥彻底不再移动任何地图标记"的体制下**已无调用方**（T1-T8 归玩家、T9/T10 归 FCS 自动控制）。
见 openQuestions —— 默认按删除处理。

### 1.6 实体读取（ReadEntities）

遍历 `Fire Mission Root` 的**直接子物体（一层，不递归）**：

- 取 `EntityLocation` 组件，无则跳过。
- 取 `loc.Entity`（`MapEntity`）；为 null（尚未初始化）跳过，**读取要 try/catch**。
- 位置：`Draggable Surface.InverseTransformPoint(child.position)` → map-local。
- **可见性判定**（迷雾）：
  `visible = loc.VisualRoot != null && loc.VisualRoot.activeInHierarchy`；
  若 `visible` 且 `loc.VisibilityGroup != null`，再加条件 `loc.VisibilityGroup.alpha > 0.05f`。
  任何异常一律判为不可见。
- `includeHidden == false`（默认）时丢弃不可见实体。

`MapEntityDto` 字段与来源（字段名即 JSON 字段名，序列化时 camelCase）：

| DTO 字段 | 来源 | 说明 |
|---|---|---|
| `Id` | `entity.ID ?? child.name` | 对 LLM 的目标寻址键 |
| `RawId` | `entity.RawID ?? ""` | 用于平民/医院识别 |
| `Role` | `((Il2Cpp.EntityRoles)entity.Role).ToString()` | 字符串枚举名 |
| `RoleValue` | `(int)entity.Role` | |
| `State` | `((Il2Cpp.MapEntityStates)entity.State).ToString()` | |
| `StateValue` | `(int)entity.State` | |
| `Health` / `MaxHealth` / `Armour` / `Stars` | 同名成员 | int |
| `IsAlive` | `entity.IsAlive` | |
| `Visible` | 见上 | |
| `ImmuneShells` | `entity.ImmuneShells.ToArray()`，失败为空数组 | string[] |
| `MapX` / `MapY` | map-local | 非 km |
| `BearingDeg` / `DistanceKm` | 相对炮位棋子解算 | 仅态势感知，权威解算归 FCS |

`FindEntity(entityId)`：在 `ReadEntities()`（默认，仅可见）结果中，取第一个
`Visible == true 且 (Id == entityId || RawId == entityId)`。区分大小写（原实现用 `==`）。

### 1.7 实体轮询与事件（PollAndEmitEvents）

节律由上层驱动：`MapPollSeconds = 0.5f`（与弹着轮询同一拍）。

- 用 `includeHidden: true` 取**全表**做状态跟踪（否则实体一进雾就"消失"，再出雾会误报 revealed）。
- 上一帧快照按 `Id` 建字典 `_previous`。
- 事件只对**当前可见**实体发出：

| 条件 | 事件 type | source | 文本格式（逐字） |
|---|---|---|---|
| 现在可见，且 `prev == null` 或 `!prev.Visible` | `entity_revealed` | `map` | `{Id} ({Role}) revealed at bearing {BearingDeg:F1}°, {DistanceKm:F2} km` |
| `prev != null` 且现在可见，且 `\|ΔMapX\| + \|ΔMapY\| > 0.01f` | `entity_moved` | `map` | `{Id} moved to bearing {BearingDeg:F1}°, {DistanceKm:F2} km` |
| `prev != null` 且现在可见，且 `Health < prev.Health && IsAlive` | `entity_damaged` | `map` | `{Id} damaged: {Health}/{MaxHealth}` |
| `prev != null && prev.Visible && prev.IsAlive && !IsAlive` | `entity_destroyed` | `map` | `{Id} destroyed` |

  注意：`entity_revealed` 与 `entity_moved`/`entity_damaged` 是 if/else 关系（同一帧只可能出其一类）；
  `entity_destroyed` 是独立判断，可与前者同帧发出。
- 移动阈值 `0.01f` 是 **map-local** 单位（≈38m）。
- 每条事件都带 `data = MapEntityDto`（`EventLog.Append` 的第 4 参数）。
- **摧毁事件要求 `prev.Visible`**：雾中被摧毁的目标不会报 destroyed（等它再次可见时 prev.IsAlive 已为 false）。
  这是既有行为，重实现如要改必须是有意识的决定。

---

## 2. GunStateReader —— 炮体状态

无状态静态读取。**不经 FCS**，直读游戏对象，因此 FCS 的 F9 热重载不影响它。

- `Read(side)`，`side ∈ {"Left", "Right"}`：`GameObject.Find("Gun" + side)` → `GunController` 组件。
- 找不到：返回 `GunDto { Side = side, Bound = false }`，其余为默认值。
- 找到：`Bound = true`，逐字段 try/catch 读：

| DTO 字段 | 来源 |
|---|---|
| `ChamberedShell` | `gun.ChamberedShellBlueprint.shellDefinition.ShellId`（两级 null 检查后才取） |
| `PowderCharges` | `gun.PowderCharges` |
| `CanFire` | `gun.CanFire` |
| `IsReloading` | `gun.IsReloading` |
| `CurrentElevation` | `gun.CurrentElevation`（度） |

- `ReadBoth()` 返回顺序固定 `[Left, Right]`。
- 注意：`ChamberedShell` 是**原始** `ShellId`（未做 SMOKE→SMK / 去 "Shell" 归一化），
  与 `AmmoReader` 的归一化 id 不同名。上层比较时要注意。

---

## 3. AmmoReader —— 征用台弹药与征用点

无状态（除规格缓存）。

### 3.1 卡片清单（ReadCards）

- 权威来源 = **物理上摆在征用台上的打孔卡**，即本关允许购买的全部类型。
- `GameObject.Find("Requisition Console")`，不存在返回空表（不报错）。
- `console.transform.GetComponentsInChildren<PunchcardRuntime>(true)`（**include inactive = true**），
  整个调用包 try/catch，异常返回空表。
- 每张卡：`card.CurrentDefinition`（`PunchcardDefinitionV2`），读失败 `continue`。
- 过滤：`def == null`、`ID` 空白、`ID == "PowderCharges"`（火药是耗材不是弹种）→ 跳过。
- **id 归一化（逐字）**：`id.Replace("SMOKE", "SMK").Replace("Shell", "").Trim()`；
  归一化后长度 0 或已存在同名 → 跳过（先到先得去重）。
- `CardDto { Id, Cost, RemainingUses, IsRecon }`，后三者各自 try/catch。
- `ReadAvailableShells()` = `ReadCards()` 的 Id 列表。

### 3.2 征用点余额（ReadRequisitionPoints）

- `MissionStatsTracker.Instance.requisitionPoints`（Int32，游戏侧 ProtectedInt 防篡改）。
- tracker 为 null 或任何异常 → 返回 `null`（"读不到"必须与"余额 0"严格区分：读不到时上层一律放行）。

### 3.3 弹种静态规格（ReadShellSpecs）

- 来源：`Resources.FindObjectsOfTypeAll(Il2CppType.Of<ShellDefinition>())`（资产级扫描）。
- `def.ShellId` 空白跳过；同 §3.1 的归一化与去重。
- `ShellSpecDto` 字段（各自 try/catch）：

| 字段 | 来源 | 单位 |
|---|---|---|
| `Damage` | `def.Damage` | 抽象伤害值 |
| `ImpactRadius` | `def.ImpactRadius` | **km** |
| `ProjectilesPerShell` | `def.projectilesPerShell` | 子弹药数 |
| `MaxCharges` | `def.maxPowderCharges` | 装药数 |
| `ChargeRanges[]` | `def.chargeRangeMappings` 逐项 | `{ Charge = m.chargeLevel, MinKm = m.minRange, MaxKm = m.maxRange }` |

- **缓存**：结果非空时写入静态缓存，之后直接返回缓存。资产数据不变是这条缓存的前提。
  缓存**没有**在 F9/换关时清除（见 openQuestions）。

---

## 4. ImpactReader —— 真实弹着与游戏自带修正提示

有状态实例。核心价值：**"意图瞄点 vs 实际弹着"的系统性偏差 == 假定炮位误差**，
是 agent 做校射（registration fire）的唯一依据。

状态三件（`Reset()` 必须同时清空，供 F9/新任务用）：
按 markerDataList 索引记的上次弹着 local 坐标、上次 marker 实例 id、已上报过的修正提示实例 id 集合。

### 4.1 弹着轮询（PollAndEmitEvents）

签名契约：`PollAndEmitEvents(Transform? mapSurface, Func<float, float, string?>? resolveImpact)`。
`mapSurface` 由 `MapReader.MapSurface` 提供，为 null 直接返回。

- `ImpactMarkerManager.Instance`（try/catch），或 `markerDataList == null` → 返回。
- 按**索引 i** 遍历 `markerDataList`：
  - `data.activeMarkerInstance` 为 null 或 `!activeInHierarchy` → 跳过。
  - `instanceId = instance.GetInstanceID()`；`instanceChanged = 无历史记录 || 与上次不同`；随即更新记录。
  - `local = mapSurface.InverseTransformPoint(instance.transform.position)`。
  - **"新弹着"判据**：`instanceChanged == true`，**或**位置变化超阈值
    （`|Δx| >= 0.01f || |Δy| >= 0.01f`，map-local）。两者皆否 → 跳过。
    实例变化必须参与判据：同一位置的重复射击会重生成/重绑 marker 而**不移动**它。
  - 转 km；`gunName = data.gun.gameObject.name`，任何异常回退 `$"gun{i}"`。
  - 调用 `resolveImpact(kmX, kmY)`（异常吞掉当作 null）。该回调由上层实现：在 3km 内匹配最近的在途炮弹并销账，
    返回其身份串（形如 `#12 K4 5:0 (HE)`）。
  - 发事件 `shell_impact` / source `map`，文本逐字：

    ```
    实际弹着({gunName}): km({kmX:F2},{kmY:F2}) [{grid}]
    ```

    其中 `grid = Agent.GridMath.GridOf((kmX, kmY))`；若回调返回非 null，**追加**：

    ```
     → 在途任务 {settled} 已落地销账
    ```

- 遍历结束后必须调用修正提示轮询（§4.2）。

### 4.2 弹着修正提示（黄箭头）

游戏自己的脱靶反馈：每次弹着生成一个 `ImpactVisualCorrections`，给玩家显示一个指向最近目标的
黄箭头 + 一段粗略距离文字。两者**故意不精确**（方向按档位加随机误差，距离量化）。

**保密不变量：只转述玩家可见的保真度**。绝不能输出目标真实坐标、误差档位或误差参数——
那是玩家没有的情报，泄露即开图作弊。

流程：

- `UnityEngine.Object.FindObjectsOfType<ImpactVisualCorrections>()`，异常直接返回。
- 每个 hint：读 `GetInstanceID()`、`_initialEvaluated`、`_isHit`，异常 `continue`。
- `!_initialEvaluated` → 跳过（游戏还没判完）。
- 已上报集合去重：`Add(key)` 返回 false → 跳过（每个提示只播报一次）。
- 读 `_impactLocalPos`（Vector2）；读失败要**把 key 从已上报集合中移除**再 `continue`（留待下次重试）。
- `at = $"km({kmX:F2},{kmY:F2}) [{grid}]"`。
- **命中分支**（`_isHit == true`）：发 `impact_hint` / `map`，文本逐字：

  ```
  弹着确认: {at} 命中(爆炸半径内有目标, 无修正提示)
  ```

  然后 `continue`。
- **脱靶分支**：
  - `hint._currentTarget == null` → `continue`（游戏也不画箭头，无可转述）。
  - `dx = _currentTargetLocalPos.x - _impactLocalPos.x`，`dy` 同理；
    `dx*dx + dy*dy < 1e-8f` → `continue`。
  - `displayedBearing = Atan2(dx, dy) * Rad2Deg`；若 `hint._errorOffsetValid` 则
    `+= hint._directionErrorOffsetDeg`；再规范化 `(v % 360 + 360) % 360`。
    （偏移的符号约定无所谓——真值无论如何落在 ±误差内。）
  - `range = hint.rangeText?.text ?? ""`（try/catch）。
    `distance` 段：`range` 空白 → 空串；否则 `$", 距离（不准确）\"{range.Trim()}\""`。
  - 发 `impact_hint` / `map`，文本逐字（注意"（不准确）"用的是全角括号，位置在角度数值**前面**）：

    ```
    弹着修正提示(黄箭头): 脱靶弹着 {at} → 附近目标在方位约 （不准确）{displayedBearing:F0}° 方向{distance}
    ```

---

## 5. MapDrawer —— 用玩家自己的绘图工具作图

静态。作图走**物理正统**：`MapMarkerPlacer` 实例方法 `RestoreMarker(MapMarkerSaveData)`，
即游戏从存档恢复玩家手绘笔迹/圆规圈的同一条管线。

### 5.1 绘制（Draw）

签名：`Draw(int placerIndex, string prefabName, Vector2 origin, Vector2 target)` → 结果字符串。

- **存档坐标 == km 帧**（实测标定）。传进来的 origin/target 就是 km。
- `UnityEngine.Object.FindObjectsOfType<MapMarkerPlacer>(true)`；长度 0 → 返回 `no MapMarkerPlacer in scene`。
- 选 placer：`placerIndex` 在 `[0, len)` 内取该项，否则回退 `placers[0]`。
- 构造 `MapMarkerSaveData { PlacerIndex, PrefabName, Origin, Target }` 并调用
  **实例方法** `placer.RestoreMarker(save)`（追加语义）。
- **绝对禁止**用静态 `MapMarkerPlacer.RestoreMissionMarkers(list)` 逐条画：它是"清空后整体恢复"，
  会连玩家手绘一起洗掉。
- 成功返回 `ok`。异常：`MelonLogger.Warning($"[AgentBridge] map draw failed: {ex.Message}")`，
  返回 `$"draw failed: {ex.Message}"`。

**prefab 名（逐字）与语义**：

| prefabName | 用途 |
|---|---|
| `MapMarkerYellow` | 黄笔——观测线（from → to） |
| `MapMarkerDiscCompass` | 圆规——`Origin = 圆心`，`Target = 半径端点`（画圆时取 `(cx + r, cy)`） |
| `MapMarkerRED` | 红笔——解算结果点，**点 = 零长度笔画**（`Origin == Target`） |
| `MapMarkerWhite` | 白笔（存在，当前无调用方） |

上层 `solve_target` 自动作图的调用惯例（跨模块契约）：所有几何线用 `MapMarkerYellow`、
圆用 `MapMarkerDiscCompass`、解出的交点用 `MapMarkerRED` 零长笔画，`placerIndex` 一律 `0`。

### 5.2 巡检（Inspect）

返回 `{ placers, captured }`：

- `placers[]`（对每个 `MapMarkerPlacer`，include inactive）：
  `{ name = gameObject.name, path = MapMarkerPlacer.GetHierarchyPath(transform),
     active = isActiveAndEnabled, prefabs = [...], placed = placedMarkers?.Count ?? 0 }`
  - `prefabs`：先 `knownMarkerPrefabs` 的 name，再 `markerPrefabs` 中尚未出现的 name（去重合并）；
    整段包 try/catch。
- `captured[]`：`MapMarkerPlacer.CaptureMissionMarkers()` 逐项
  `{ placerIndex, prefabName, origin = {x, y}, target = {x, y} }`（Origin/Target 转 `Vector2`）。
  异常时**不抛出**，向该数组追加一项 `{ error = ex.Message }`。

### 5.3 清除（ClearAll）

对所有 placer 调 `ClearPlacedMarkers()`（每个各自 try/catch，成功计数）。
返回 `$"cleared markers on {cleared} placer(s)"`。
**会一并清掉玩家手绘**，只在明确请求（`POST /draw/clear`）时执行，绝不自动调用。

---

## 6. RequisitionOperator —— 征用台物理购买

静态，有全局忙标志。**职责边界**：非炮弹卡（侦察/特种卡）的**回退**购买路径；
炮弹购买归 FCS 的 `PurchaseDeck`。正道是经 `FSC.RequestConsoleCard(...)` 的 DTO 优先队列，
只有 FCS 缺该 API 时才落到本操作器。

### 6.1 状态与注入

- `Busy`（bool，只读对外）、`LastResult`（string，只读对外）。
- `RequisitionLockProvider`：`Func<object?>`，由 mod 在初始化时注入
  `() => _fcs.GetRequisitionLock()`——解析 FCS `SharedResources.Requisition`（Logic ALC 里的
  `CoroutineLock`）。**每次调用重新解析，绝不跨 F9 缓存**。FCS 未加载时返回 null，此时无锁裸奔。

### 6.2 常量

- 卡槽世界坐标 `CardSlot = new Vector3(6.4814f, -2.4675f, -22.0968f)`（逐字保留）。

### 6.3 找卡（FindCard）

- 根 `Requisition Console`，`GetComponentsInChildren<PunchcardRuntime>(true)`。
- `CurrentDefinition?.ID` 空白跳过；**available 列表收集原始 ID（未归一化）**，用于失败回执。
- 匹配（均 `OrdinalIgnoreCase`）：原始 ID 相等，**或**归一化后
  （`Replace("SMOKE","SMK").Replace("Shell","").Trim()`）相等。

### 6.4 发起购买（StartPurchase）

`StartPurchase(cardId, bearingDeg?, distanceKm?)`，主线程 only，**非阻塞**（启协程后立即返回）。
返回串逐字：

- 已忙：`requisition operator busy with a previous card`
- 找不到卡：`card '{cardId}' not on the console; available: [{原始ID 以 ", " 连接}]`
- 正常：`started (physical purchase takes ~4s; watch events for the outcome)`

### 6.5 购买协程时序（逐步，含每个 wait 的秒数）

1. **取锁**：`RequisitionLockProvider()`（异常→null）。非 null 时反射取 `Acquire()` 方法，
   把返回值当 `IEnumerator` `yield return`（等到持锁）。反射失败则置 null 继续无锁。
2. **插卡**：`card.position = CardSlot`；取 `DraggableItem` 组件，无 → `Finish(cardId, "card has no DraggableItem")` 并结束。
3. `draggable.MoveToSlot()`；`WaitForSeconds(0.6f)`。
4. **拨盘**（仅当 `bearingDeg` 或 `distanceKm` 非空）：
   - 轮询 `FindObjectOfType<DialOdometerPunchcardBridge>()`，最长 **4s**（`Time.realtimeSinceStartup` 计时），
     每次未命中 `WaitForSeconds(0.25f)`。
   - 仍无 → `Finish(cardId, "card accepted but no bearing/distance controls appeared (not a recon card?)")` 并结束。
   - **bearing 三段式**：
     a. `bridge.bearingDial?.SetDialValue(b)`（物理优先），`WaitForSeconds(0.3f)`；
     b. 回读 `bridge.Bearing`（异常→NaN）；
     c. 若 NaN 或 `|Mathf.DeltaAngle(applied, b)| > 1f`（度）→
        `bridge.SetBearingInternal(b, true)` + `bridge.ForceRefreshAll()` + 再回读（整段 try/catch 吞异常）。
     d. 日志：`[AgentBridge] scout bearing requested {b:F1}° applied {applied:F1}°`
        事件 `requisition` / source `console`：`scout bearing set: requested {b:F1}°, applied {applied:F1}°`
   - **distance**：`bridge.distanceDial?.SetDialValue(d)`（**无回读校验**，与 bearing 不对称）。
   - `WaitForSeconds(0.5f)`。
5. **找买钮**：`Requisition Console` 的子物体 `Universal Button` 上的 `LookAtTarget` 组件。
   无 → `Finish(cardId, "buy button not found")` 并结束。
6. **等按钮可点**：最长 **10s**，条件为 `button.isActive && Time.realtimeSinceStartup >= button.nextAllowedClickTime`；
   每帧 `yield return null`。
   超时仍不可点 → `Finish(cardId, "FAILED: buy button never became active — purchase NOT made, retry later")` 并结束。
   **不变量：绝不点死按钮**——按下无效按钮会静默不买，却让我们上报成功。
7. **点击**：`OnClickDown()` → `WaitForSeconds(0.2f)` → `OnClickUp()` → `WaitForSeconds(2f)`
   → `Finish(cardId, "ok (button pressed while active)")`。
8. **finally（无论走哪条分支都必须执行）**：若持锁则反射 `Release()`（异常吞掉）；`Busy = false`。

### 6.6 收尾（Finish）

三处同时落账，格式逐字：

- `LastResult = result`
- 日志：`[AgentBridge] requisition '{cardId}' -> {result}`
- 事件：type `requisition`，source `console`，文本 `requisition card '{cardId}' -> {result}`
- 交易账：`Agent.TransactionLog.Write("requisition", $"{cardId} -> {result}")`

### 6.7 控制台巡检（InspectConsole）

逆向用途：把征用台层级结构（对象名、组件、卡 id）dump 出来，便于发现新控件。

- 两个根：`Requisition Console`、`Console Box`。根不存在 → 该项为 `{ root = <名>, error = "not found" }`。
- 递归深度上限 **6**（`depth > 6` 直接返回）。
- 组件过滤：跳过类型名为 `Transform` / `MeshFilter` / `MeshRenderer` / `BoxCollider` / `MeshCollider` 的。
  类型名取 `c.GetIl2CppType().Name`，取不到就跳过该组件。
- 附注：`DialInteractable` 追加 `" value?"`（**占位，未实现读值**）；
  `PunchcardRuntime` 追加 `$" id={punch.CurrentDefinition?.ID}"`。
- 只收录组件列表非空的节点，形如 `{ path, comps }`；path 从根名开始用 `/` 拼接。
- 返回根数组 `[{ root, nodes }, ...]`。

---

## 7. SceneFinder —— 场景对象搜索（调试）

静态，纯只读。

- `Find(nameSubstring)`：遍历 `Resources.FindObjectsOfTypeAll<Transform>()`。
- 过滤：`t == null` 或 `!t.gameObject.scene.IsValid()` 跳过（滤掉 prefab/资产，只留场景实例）。
- 名称匹配：`t.name.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0`。
- path：从自身向上拼父名，最多 **12** 层，`/` 连接。
- 每条结果：

  ```
  { path, active = gameObject.activeInHierarchy,
    world = { x, y, z }            // 各 Math.Round(..., 3)
    mapLocal = { x, y, kmX, kmY }  // x/y Round 3；kmX/kmY Round 2；仅当 Draggable Surface 存在
  }
  ```

- **硬上限 60 条**，达到即 break（无分页）。返回 `{ count, hits }`。
- 查询串长度校验（≥3）在 HTTP 层做，本函数不校验。

---

## 8. ScoutPlaneOperator —— 侦察机 prefab 直生（备胎）

静态。**这是备胎路径**；正道是买 `ScoutPlane` / `ScoutPlane_OnTimeUse` 打孔卡（见 §6 与征用台学说）。

`Spawn(kmX, kmY, bearingDeg)` 返回匿名对象：

1. `Draggable Surface` 缺失 → `{ error = "Draggable Surface not found (scene unbound?)" }`
2. 取模板：遍历 `Resources.FindObjectsOfTypeAll(Il2CppType.Of<Il2CppSleepyNodes.State_SpawnScoutPlane>())`，
   取**第一个** `PlanePrefab != null` 的节点；记 `templateName = spawn.name`。
   无 → `{ error = "no State_SpawnScoutPlane asset with a PlanePrefab is loaded in this mission" }`
3. km → map-local → `surface.TransformPoint(local)` 得 world 位置。
4. `Instantiate(prefab)`；`instance.name = "AgentBridge ScoutPlane"`；置 position；
   `rotation = surface.rotation * Quaternion.Euler(0f, 0f, -bearingDeg)`
   （0° = 地图北 +Y local，顺时针；负号即由此而来）；`SetActive(true)`。
5. 注册进侦察系统：`ImpactMarkerManager.Instance?.RegisterScoutPlane(instance, "AgentBridge")`；
   失败不致命，只 `MelonLogger.Warning($"[AgentBridge] RegisterScoutPlane failed: {ex.Message}")`。
6. 收集 `instance.GetComponentsInChildren<Component>(true)` 的去重 Il2Cpp 类型名清单（供逆向）。
7. 三处落账（格式逐字）：
   - 日志：`[AgentBridge] scout plane spawned from '{templateName}' at km({kmX:F2},{kmY:F2}) brg {bearingDeg:F0}`
   - 事件 `scout_plane` / source `map`：`scout plane launched at km({kmX:F2},{kmY:F2}) bearing {bearingDeg:F0}°`
   - 交易账：`TransactionLog.Write("scout_plane", $"spawned at km({kmX:F2},{kmY:F2}) brg {bearingDeg:F0}", new { templateName, components })`
8. 返回 `{ result = "ok", templateName, world = { x, y, z }, components }`。

飞行与揭雾行为完全由 prefab 自身组件驱动，本模块不实现。

---

## 9. SignalOperator —— 地堡号角

静态。物理正统：**任务通知绝不直接注入**——场景里没有号角 = 发不出信号。

### 9.1 定位（FindHorn）

- 关键词表逐字：`{ "horn", "signal", "siren", "klaxon", "bugle" }`，`OrdinalIgnoreCase` 子串匹配。
- 遍历 `Resources.FindObjectsOfTypeAll(Il2CppType.Of<LookAtTarget>())`；
  `!gameObject.scene.IsValid()` 跳过（滤 prefab）。
- 匹配对象是**对象路径**，路径由 `ObjectPath(transform, 3)` 生成：自身 + 最多 2 层父，`/` 连接。
- 命中的路径进 `candidates`（out 参数）；`best` 取**第一个**命中者。
- **无命中时的自诊断**：进程内**只做一次**（一次性标志），输出全量交互件清单，格式逐字：

  ```
  [AgentBridge] no horn-like LookAtTarget; scene has {all.Count}: {前 60 条路径以 " | " 连接}
  ```

  用途：据此日志识别真实道具名并补进关键词表（关键词是否命中尚未实测，见 openQuestions）。

### 9.2 拉响协议（对外行为，实现在 mod 层但属本模块契约）

- 未找到 → 返回：`本关场景中没有找到号角装置(无匹配horn/signal/siren的交互件) — 无法发出信号`
- `!horn.isActive` → 返回：`号角 '{horn.gameObject.name}' 当前不可交互 — 可能尚未满足拉响条件`
- 正常：`horn.OnClickDown()` → 启协程 `WaitForSeconds(0.15f)` → `horn.OnClickUp()`（try 包裹）。
  这是"玩家注视点击"走的同一条事件路径。
- 事件 `signal` / source `game`：`号角已拉响: {name}{extra}`，
  其中 `extra` 仅当 `candidates.Count > 1` 时为 `$" (场景候选: {以 ", " 连接})"`，否则空串。
- 返回：`号角已拉响: {horn.gameObject.name}`。
- 主线程 only。

---

## 10. TeleprinterReader —— 双电传打字机

有状态实例（每台机器记住上次全卷文本）。

| 枚举 | 语义 |
|---|---|
| `Teleprinter.Teleprinters.Primary` | 最高统帅部电文（High Command 任务指令） |
| `Teleprinter.Teleprinters.Secondary` | 战场报告（观测员等回报） |

### 10.1 读取

- 取机器：`Teleprinter.GetTeleprinter(which)`，异常 → null。
- `TeleprinterDto`：`Which = "primary" | "secondary"`（小写字符串，即对外契约）；
  `Bound = printer != null`；`FullText = StripRich(printer.CaptureMissionState()?.CurrentFullRich ?? "")`。
  读取全卷时异常 → **把 `Bound` 改回 false**（而非抛出）。
- **富文本剥离正则（逐字）**：`<[^>]{1,64}?>`，`RegexOptions.Compiled`。
  `StripRich(rich)` = 用该正则替换为空串，再 `Replace("\r", "")`，再 `Trim()`。
- `ReadAll()` 返回顺序固定 `[Primary, Secondary]`。

### 10.2 增量轮询（PollAndEmitEvents）

节律由上层驱动：`TelegraphPollSeconds = 1.0f`。**不要求场景已绑定**（与地图轮询独立）。

对两台机器分别做全卷 diff：

- `!Bound` 跳过；`text == last` 或 `text.Length == 0` 跳过。
- delta 判定（顺序不可换，比较均用 `StringComparison.Ordinal`）：
  1. `text.Length > last.Length && text.StartsWith(last)` → `delta = text[last.Length..].Trim()`（尾部新增）
  2. `text.Length > last.Length && text.EndsWith(last)` → `delta = text[..(text.Length - last.Length)].Trim()`（头部新增）
  3. 否则 → `delta = text`（整卷被清空/替换，全量重发）
- 无论是否发事件，都要更新 `last = text`。
- `delta.Length > 0` → 发事件：type `telegraph_message`，
  **source = `"primary"` 或 `"secondary"`**（注意：电文事件的 source 是机器名，不是 `"map"`/`"fcs"`），
  text = delta。
- `Reset()` 清空全部 last（换场景 / F9 / 新任务时调用）。清空后下一次轮询会把整卷当作新消息重发一遍——
  这是**有意**的：重启后的 agent 从活的现实重建认知，而不是回放陈旧历史。

### 10.3 回打电文（Print）

- `Print(which, IEnumerable<string> lines)` → bool。机器不存在返回 false。
- 把 lines 逐行装入 `Il2CppSystem.Collections.Generic.List<string>`，调用
  `printer.SubmitLines("AgentBridge", <cast 成 Il2CppSystem.Collections.Generic.IEnumerable<string>>)`；
  **作者名固定字符串 `"AgentBridge"`**。
- 随后 `printer.TryStart(ignoreInitialDelay: true)`，返回 true。
- 上层选机：`which` 等于 `"primary"`（忽略大小写）→ Primary，**其余一律 Secondary**（默认回打战场报告机）。

---

## 11. 跨模块契约

### 11.1 本模块向外暴露

**DTO（字段名即 HTTP JSON 字段名；序列化策略 camelCase、忽略 null、UnsafeRelaxedJsonEscaping）**

- `MapEntityDto`、`MarkerDto`、`GunDto`、`TeleprinterDto`、`CardDto`、`ShellSpecDto`、`ChargeRangeDto`
  （定义见 dtos 模块，本模块负责填充）。

**给 mod / agent 的方法契约**

| 提供方 | 契约 | 消费方与用途 |
|---|---|---|
| `MapReader.IsBound` | bool | HUD 显示门控、轮询门控、fire/adjust 前置检查（未绑定回 `tactical map not bound`） |
| `MapReader.MapSurface` | `Transform?` | 传给 `ImpactReader.PollAndEmitEvents` |
| `MapReader.KmBounds` | `(MinX,MinY,MaxX,MaxY)?` | 喂 `GridMath.SetMapBoundsKm`；null 时 `GridMath.ResetMapBounds()` |
| `MapReader.TurretLocalOnMap()` | map-local | 射击原点：fire/adjust 解算、`get/set_assumed_turret_position`、手动校准检测 |
| `MapReader.ReadEntities()` | 仅可见 | 快照、友军/平民普查、盲射判定 |
| `MapReader.FindEntity(id)` | 仅可见 | `fire`/`adjust`/`firing_solution` 的 entityId 路径 |
| `AmmoReader.ReadCards()` / `ReadAvailableShells()` | | 快照 `cards`/`availableShells`、预算门 |
| `AmmoReader.ReadShellSpecs()` | `ImpactRadius` 单位 km | 快照 `shellSpecs`（按在售卡过滤）、爆炸半径普查、最大射程校验 |
| `AmmoReader.ReadRequisitionPoints()` | `int?` | 快照 `requisitionPoints`、事件余额后缀 `· 征用点余额 N`、特殊卡预算门 |
| `GunStateReader.ReadBoth()` | | 快照 `guns` |
| `TeleprinterReader.ReadAll()` / `Print()` | | 快照 `teleprinters`、`POST /print` |
| `RequisitionOperator.StartPurchase()` | | FCS 无 `RequestConsoleCard` API 时的回退购买 |
| `MapDrawer.Draw()` | km 帧 | `solve_target` 自动作图（`MainThread.Post`，绝不阻塞 agent） |

### 11.2 本模块依赖外部

- `EventLog.Append(type, source, text, data = null)` —— 本模块产出的事件类型/来源全表：

  | type | source | 产出方 |
  |---|---|---|
  | `entity_revealed` / `entity_moved` / `entity_damaged` / `entity_destroyed` | `map` | MapReader |
  | `shell_impact` | `map` | ImpactReader |
  | `impact_hint` | `map` | ImpactReader |
  | `telegraph_message` | `primary` / `secondary` | TeleprinterReader |
  | `requisition` | `console` | RequisitionOperator |
  | `scout_plane` | `map` | ScoutPlaneOperator |
  | `signal` | `game` | 号角（mod 层调用 SignalOperator） |

  （其余类型 `fcs_task_update`/`shell_fired`/`friendly_warning`/`counter_battery`/`commander_order`/
  `cinematic`/`turret_position` 由别的模块产出，本模块不得占用。）
- `Agent.GridMath.GridOf((kmX, kmY))` —— 弹着事件里的网格串。
- `Agent.GridMath.SetMapBoundsKm / ResetMapBounds` —— 图幅回灌。
- `Agent.TransactionLog.Write(kind, text, data?)` —— `requisition`、`scout_plane`。
- `MainThread.Run / Post / Pump`。
- `FcsGateway.GetRequisitionLock()` —— 经 `RequisitionOperator.RequisitionLockProvider` 注入。
- `MelonLogger.Msg / Warning`，`MelonCoroutines.Start`。

### 11.3 HTTP 端点（本模块直接服务的）

监听 `http://127.0.0.1:17171/`（仅回环）。

| 方法 路径 | 请求体 / 查询 | 走向 |
|---|---|---|
| `GET /markers` | — | `MapDrawer.Inspect()` |
| `POST /draw` | `{placerIndex, prefabName, ox, oy, tx, ty}`；`prefabName` 缺失 → 400 `need {placerIndex, prefabName, ox, oy, tx, ty}` | `MapDrawer.Draw` → `{result}` |
| `POST /draw/clear` | — | `MapDrawer.ClearAll()` → `{result}` |
| `POST /turret` | `{kmX, kmY}`；解析失败 → 400 `need {kmX, kmY}` | `SetDeclaredTurret` → `{result}` |
| `POST /requisition` | `{cardId, bearingDeg?}`；`cardId` 缺失 → 400 `need {cardId, bearingDeg?}` | `RequisitionOperator.StartPurchase(cardId, bearingDeg, null)`，**主线程超时放宽到 15000ms** → `{result}` |
| `GET /console` | — | `RequisitionOperator.InspectConsole()` |
| `GET /find` | `?q=<子串>`，`q.Length < 3` → 400 `need ?q=<name substring, >=3 chars>` | `SceneFinder.Find(q)` |
| `POST /scoutplane` | `{kmX, kmY, bearingDeg}`；解析失败 → 400 `need {kmX, kmY, bearingDeg}` | `ScoutPlaneOperator.Spawn` |
| `POST /horn` | — | 号角（`SignalOperator.FindHorn` + 点击协程）→ `{result}` |
| `POST /print` | `{which, lines[]}`；`lines` 空 → 400 `need {which, lines[]}`；成功 200 `{result:"ok"}`，机器不可用 409 `{result:"printer not available"}` | `TeleprinterReader.Print` |

（`GET /state`、`GET /events`、`POST /fire`、`POST /adjust`、`POST /command` 由其他模块拥有，
但 `/state` 的绝大部分内容由本模块填充。）

### 11.4 轮询节律（由 mod 的 OnUpdate 驱动，本模块的时序前提）

| 常量 | 值 | 覆盖 |
|---|---|---|
| `BindRetrySeconds` | `2f` | `MapReader.TryBind` 重试 |
| `MapPollSeconds` | `0.5f` | `MapReader.PollAndEmitEvents` + `ImpactReader.PollAndEmitEvents` |
| `TelegraphPollSeconds` | `1.0f` | `TeleprinterReader.PollAndEmitEvents` |

地图轮询与弹着轮询共用同一拍，且**地图轮询失败不得阻断弹着轮询**（各自 try/catch）。
地图轮询异常日志：`[AgentBridge] map poll failed: {ex.Message}`；
电文轮询异常日志：`[AgentBridge] telegraph poll failed: {ex.Message}`；
弹着轮询异常**静默吞掉**（原实现 `catch { }`，无日志）。

### 11.5 重置语义

`Unbind()`（换场景）与全量重置（F9 / 新任务开始）时，本模块必须清空：

- `MapReader`：全部绑定引用、标记表、home 表、`_previous` 实体快照、`KmBounds`。
- `ImpactReader.Reset()`：三张状态表全清。
- `TeleprinterReader.Reset()`：两台机器的 last 文本全清。
- `AmmoReader` 的规格缓存**当前不清**（见 openQuestions）。
- `RequisitionOperator.Busy` 不参与重置（由协程 finally 负责）。

---

## 12. 不变量与防御性规则（single out）

1. **主线程 only**：本模块所有函数只能在 Unity 主线程执行。跨线程一律 `MainThread.Run/Post`。
   `MainThread.Run` 默认超时 10000ms，超时抛
   `main-thread call not serviced within {timeoutMs}ms (game unfocused or scene loading?)`；
   `/requisition` 例外，用 15000ms。
2. **Il2Cpp try/catch 粒度到字段**：每个属性读取独立 try/catch，绝不允许一个字段炸掉整条记录。
   遍历中的单项失败一律 `continue`，不中断遍历。
3. **战争迷雾**：`ReadEntities` 默认剔除不可见；`includeHidden: true` 只服务内部 diff；
   `FindEntity` 强制 `Visible == true`。任何暴露给 LLM 的路径都不得使用 `includeHidden`。
4. **弹着提示保密**：只转述玩家可见的方位扇区与距离文本原样，绝不披露目标真实坐标、误差档位或误差参数。
5. **画图只用实例 `RestoreMarker`**：静态 `RestoreMissionMarkers(list)` 是清空重建，禁止逐条调用。
   `ClearAll` 会清掉玩家手绘，只在显式请求时调用。
6. **桥不移动任何地图标记**：`MapToken_Artillery` T1-T8 归玩家手动，T9/T10 归 FCS 自动控制
   （FCS 每 0.5s 把 T9/T10 摆到左/右炮当前任务瞄点）。任何"挪标记再排任务"的旧做法都已废止，
   入队走纯坐标路径。
7. **绝不点死按钮**：`LookAtTarget` 必须 `isActive` 且过了 `nextAllowedClickTime` 才能点，
   否则静默买不到却上报成功。等待超时必须明确回报 FAILED。
8. **物理正统**：号角/购买/侦察机都走玩家能走的物理路径，不注入通知、不直接改余额。
   场景没有该道具就如实回报"做不到"。
9. **单位陷阱**：`ImpactRadius` 是 km（对外显示米要 `×1000`）；地图移动阈值 `0.01f` 是 map-local（≈38m），
   不是 km 也不是米。
10. **不缓存 Logic ALC 引用**：FCS 的 Logic 在可回收 `AssemblyLoadContext` 里，
    `RequisitionLockProvider` 每次调用都重新解析，绝不跨 F9 持有强引用。
11. **锁与忙标志必须在 finally 释放**：购买协程任何早退分支都要经 finally 归还 FCS 的 console 锁并清 `Busy`，
    否则整局再也买不了卡。
12. **一次性日志**：号角清单 dump 全进程只打一次；图幅不合理警告每次绑定都打（用于诊断）。
13. **编码陷阱（工程约束）**：含中文的源文件绝不用 PowerShell 的 `Get-Content/-replace/Set-Content` 修改
    （中文 Windows 上会 GBK 误读 UTF-8 再回写，全文乱码）。本模块的事件文本大量含中文与全角括号，
    尤其要注意。
14. **构建陷阱**：游戏运行中不可构建（Mods DLL 被锁），需 `-p:OutputPath=bin\staging\`；
    游戏运行中直接构建 `IronNestFCS.Logic` 会当场重置在用的 FCS。
15. **`GameObject.Find("TurretLocation")` 三兄弟**：真锚点 / `Canvas/MapRoot/TurretLocation` 图标 /
    `Draggable Surface/Player Turret Piece` 棋子，用途完全不同，不可互换。

---

## 13. 反射 / Il2Cpp 类型与成员清单（重实现须逐字对齐）

| 类型 | 用到的成员 |
|---|---|
| `Il2Cpp.MissionStatsTracker` | `Instance`、`requisitionPoints` |
| `Il2Cpp.ShellDefinition` | `ShellId`、`Damage`、`ImpactRadius`、`projectilesPerShell`、`maxPowderCharges`、`chargeRangeMappings`（元素含 `chargeLevel`/`minRange`/`maxRange`） |
| `Il2Cpp.PunchcardRuntime` | `CurrentDefinition` |
| `Il2Cpp.PunchcardDefinitionV2` | `ID`、`Cost`、`RemainingUses`、`IsRecon`（另有 `Prefab_ConsoleControls`） |
| `Il2Cpp.GunController` | `ChamberedShellBlueprint.shellDefinition.ShellId`、`PowderCharges`、`CanFire`、`IsReloading`、`CurrentElevation` |
| `Il2Cpp.EntityLocation` | `Entity`、`VisualRoot`、`VisibilityGroup`（`.alpha`） |
| `Il2Cpp.MapEntity` | `ID`、`RawID`、`Role`、`State`、`Health`、`MaxHealth`、`Armour`、`Stars`、`IsAlive`、`ImmuneShells` |
| `Il2Cpp.EntityRoles` / `Il2Cpp.MapEntityStates` | 枚举，`ToString()` 与 `(int)` 同时上报 |
| `Il2Cpp.ImpactMarkerManager` | `Instance`、`markerDataList`（元素含 `activeMarkerInstance`、`gun`）、`RegisterScoutPlane(GameObject, string)` |
| `Il2Cpp.ImpactVisualCorrections` | `_initialEvaluated`、`_isHit`、`_impactLocalPos`、`_currentTarget`、`_currentTargetLocalPos`、`_errorOffsetValid`、`_directionErrorOffsetDeg`、`rangeText.text` |
| `Il2Cpp.MapMarkerPlacer` | 实例 `RestoreMarker(MapMarkerSaveData)`、`ClearPlacedMarkers()`、`knownMarkerPrefabs`、`markerPrefabs`、`placedMarkers`、静态 `GetHierarchyPath(Transform)`、静态 `CaptureMissionMarkers()`、（禁用）静态 `RestoreMissionMarkers` |
| `Il2Cpp.MapMarkerSaveData` | `PlacerIndex`、`PrefabName`、`Origin`、`Target` |
| `Il2Cpp.DraggableItem` | `MoveToSlot()` |
| `Il2Cpp.DialOdometerPunchcardBridge` | `bearingDial`、`distanceDial`、`Bearing`、`SetBearingInternal(float, bool)`、`ForceRefreshAll()`（拨盘自身有 `SetDialValue(float)`） |
| `Il2Cpp.DialInteractable` | 仅类型识别（InspectConsole） |
| `Il2Cpp.LookAtTarget` | `isActive`、`nextAllowedClickTime`、`OnClickDown()`、`OnClickUp()` |
| `Il2Cpp.Teleprinter` | 静态 `GetTeleprinter(Teleprinters)`、`CaptureMissionState().CurrentFullRich`、`SubmitLines(string, IEnumerable<string>)`、`TryStart(bool ignoreInitialDelay)`；枚举 `Teleprinter.Teleprinters.Primary/Secondary` |
| `Il2CppSleepyNodes.State_SpawnScoutPlane` | `PlanePrefab`、`name` |
| `Il2CppTMPro.TextMeshPro` | `.text`（标记编号） |
| FCS（反射，跨 ALC） | `SharedResources.Requisition`（`CoroutineLock`），成员 `Acquire()`（返回 `IEnumerator`）、`Release()` |

**按名查找的场景对象（字符串逐字）**：
`TurretLocation`、`Draggable Surface`、`Fire Mission Root`、`MapToken_Artillery`、
`Player Turret Piece`、`GunLeft`、`GunRight`、`Requisition Console`、`Console Box`、
`Universal Button`。

---

## 逐字保留数据块

本模块**不含**大段自然语言数据块（agent SystemPrompt、MapIntelTable 关卡情报、学说文本均在 agent 模块）。
本模块所有需要逐字保留的内容——事件文本模板、日志格式、返回串、正则、数值常量——已在上文各节内
原样记录，无需另行搬运。
