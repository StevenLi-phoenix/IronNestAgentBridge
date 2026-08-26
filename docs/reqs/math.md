# 模块 math — 网格坐标数学与角度制计算器

对应旧实现：`agent/GridMath.cs`、`agent/Calculator.cs`。
本模块是**纯 BCL 计算模块**：不碰 Unity、不碰 Il2Cpp、不反射、不做 I/O。
唯一的可变状态是地图边界（静态、进程全局）。

---

## 1. 坐标系与单位约定（协议本体，必须逐字遵守）

### 1.1 三个坐标帧

| 帧 | 定义 | 谁负责换算 |
|---|---|---|
| map-local | `Draggable Surface` 的 localPosition（Unity 单位） | 调用方 |
| km 帧（FCS 显示帧） | `km = 10.016 + localX * 3.8164`，`km = 5.235 + localY * 3.8164` | 调用方 |
| 网格记法 | `"G6 5:3"` | 本模块 |

**本模块只在 km 帧内工作。** 常量 `3.8164`（map-local→km 比例）与偏移
`(10.016, 5.235)` **绝不出现在本模块内部**——所有进出本模块的坐标都已是 km 帧。
调用方（`AgentBridgeMod.QueueFireMission`、`FdoAgent`、`ImpactReader`、
`MapReader`、`ScoutPlaneOperator`）负责两侧换算。

### 1.2 网格记法与方向约定（**最易搞反，务必精确**）

- 字母 `A..Z` 是**横轴（x，向东增大）**，`A` 最西，`A → 0`、`B → 1`、…、`Z → 25`。
- 数字 `1..N` 是**纵轴（y，向北增大）**，**数字 1 是最南一行**（不是最上一行），
  数字越大越靠北。`行号 → 行号 - 1`。
- `子格 a:b` 中 `a` 是横向（x）子格、`b` 是纵向（y）子格，各 0..9，每格 0.1 km。
- 网格 `"A1 0:0"` 的**格原点**在 km `(0, 0)`。

**解析公式（必须给出 0.1 km 子格的格心，故 +0.05）：**

```
kmX = (letter - 'A') + subCol / 10.0 + 0.05
kmY = (row - 1)     + subRow / 10.0 + 0.05
```

**反向（km → 网格显示，必须与 FCS 的 ConvertPosition 同公式）：**

```
col   = (int)x ∈ [0, 26) ? (char)('A' + (int)x) : "#"
输出串 = $"{col}{(int)y + 1} {(int)(x * 10) % 10}:{(int)(y * 10) % 10}"
```

字母越界（x < 0 或 x ≥ 26）时列字母输出字面量 `"#"`，不得抛异常。

### 1.3 方位角约定

- **0° = 正北（+Y），顺时针增大**（90° = 正东 +X）。
- 单位方向向量：`(dx, dy) = (sin(θ), cos(θ))`，θ 为弧度。
- 由起点沿方位角偏移：`(x + sin(θ)·d, y + cos(θ)·d)`，`d` 单位 km。
- 由 (dx, dy) 反求方位角（调用方使用，本模块提供的 `calc` 需支持）：
  `bearing = atan2(dx, dy)`，负值加 360 归一。注意实参顺序是 **(dx, dy)** 而非
  数学惯例的 (dy, dx)。

---

## 2. 点解析（ParsePoint）

输入：一个字符串 `from` + 当前炮塔 km 坐标 `turretKm`。输出：km 坐标或"解析失败"。

必须按以下顺序尝试，先命中先返回：

1. **字面量 `turret`**：先 `Trim()`，再**忽略大小写**比较；命中则原样返回
   `turretKm`（不做任何边界检查）。
2. **网格记法**：用下列正则（必须**预编译**）整串匹配：

   ```
   ^\s*([A-Za-z])\s*(\d{1,2})\s+(\d)\s*:\s*(\d)\s*$
   ```

   - 字母恰好 1 个，大小写不敏感（内部转大写）；
   - 行号 1~2 位数字；
   - 字母与行号之间**允许空白**，行号与子格之间**必须至少一个空白**；
   - 子格各恰好 1 位数字，冒号两侧允许空白。
   - 命中后按 §1.2 公式换算。
3. **裸 km 对 `"kmX,kmY"`**：按 `,` 分割，**必须恰好 2 段**，两段都用
   `NumberStyles.Float` + `CultureInfo.InvariantCulture` 解析成功才接受。
   **必须用不变文化**（中文 Windows 上不得用当前区域设置）。
4. 都不匹配 → 返回"失败"（`null`），**不得抛异常**。

注意：形如 `"G6 5:3"` 之外的网格变体（缺子格的 `"G6"`、三位行号）一律走不通，
落到第 3 步失败 → 解析失败。这是既有行为，重实现不要"顺手放宽"。

---

## 3. 工具 `grid_to_km`

- 入参 JSON 字段：`grid`（string）。字段缺失或非串时按空串处理。
- 成功输出（见 §5 的标准结果对象）。
- 失败输出（错误消息**逐字**）：

  ```
  cannot parse grid '{from}' (expected like 'G6 5:3')
  ```

- 说明：本工具**只给位置，不给诸元**（射击诸元是 `firing_solution` 的职责，
  它读实时炮塔原点）。

---

## 4. 工具 `solve_target` — 交汇解算

### 4.1 入参 JSON（字段名逐字）

```jsonc
{
  "lines": [ { "from": "<点规格>", "bearingDeg": <number>, "distanceKm": <number 可选> } ],
  "circles": [ { "from": "<点规格>", "distanceKm": <number> } ],
  "near": "<点规格，可选>"
}
```

`<点规格>` 一律走 §2 的 ParsePoint（网格 / `turret` / `"kmX,kmY"`）。

解析规则：
- `lines` / `circles` 只在**存在且 ValueKind 为 Array** 时才遍历，否则视为空。
- 每条 line：`from` 缺失按空串（随后解析失败报错）；`bearingDeg` **必须存在**。
- line 的 `distanceKm` **只有 ValueKind 为 Number 时**才算"直接定位"，
  否则该 line 退化为纯方位观测线。
- 每个 circle：`distanceKm` 必须存在。
- `near` 解析失败时**静默降级为无 near**（不报错）。

### 4.2 求解优先级（严格按序，先满足先用）

1. **任一 line 带 `distanceKm` → 直接定位**：取 `directs[0]`（第一个直接定位点）。
   多个直接定位点**不做平均、不做一致性校验**，后续的被忽略。
2. **≥2 条纯方位线 → 两线交汇**：只用 `lines[0]` 与 `lines[1]`。
3. **1 条线 + ≥1 个圆 → 线圆相交**：只用 `lines[0]` 与 `circles[0]`。
4. **≥2 个圆 → 圆圆相交**：只用 `circles[0]` 与 `circles[1]`。
5. 都不满足 → 错误（消息见 §4.5）。

### 4.3 几何算法要求

**两线交汇**：以参数式求交。行列式绝对值 `< 1e-9` 判为平行。
求出两条线各自的参数 `t`（第一条）与 `s`（第二条）；
**`t < 0` 或 `s < 0` 表示交点落在观测员背后，必须判为失败**（不得返回背后解）。
错误消息要指明是"第一个"还是"第二个"观测员（`t < 0` → the first，否则 the second）。

**线圆相交**：以单位方向向量参数化（`a = 1`），判别式 `b² - 4c < 0` → 无解。
两个根 `(-b - √disc)/2`、`(-b + √disc)/2`，**只保留 t ≥ 0 的根**（观测员前方），
按此顺序装入候选（近根在前）。

**圆圆相交**：圆心距 `d`；`d < 1e-9`（同心）或 `d > r₁ + r₂` 或 `d < |r₁ - r₂|` → 无解。
标准公式求出弦心距 `l` 与半弦长 `h`（`h² ≤ 0` 时取 0）。
第一个交点为 `(mx + h·dy/d, my - h·dx/d)`；**仅当 `h > 1e-9` 时**才追加第二个交点
`(mx - h·dy/d, my + h·dx/d)`（相切只产出一解）。

**多解裁决（PickNearest）**：若未给 `near` **或**只有一个候选 → 取候选表第一个；
否则取到 `near` 欧氏距离最小者。

### 4.4 圆圆双解的歧义回执（协议本体）

当 `circles ≥ 2` 且求得 **2 个交点** 且 **未提供 `near`** 时，
**不得**擅自挑一个，必须返回下列 JSON（`note` 中文逐字）：

```json
{
  "ambiguous": true,
  "note": "两圆有两个交点, 按其他情报选择其一直接使用, 或用near重解",
  "candidates": [ { "kmX": …, "kmY": …, "grid": "…", "inMapBounds": true }, { … } ]
}
```

- 两个候选都是**完全解算好的**坐标，模型可按其他情报直接选用。
- 候选对象比标准结果多一个 `inMapBounds` 布尔字段。
- **此路径在出界检查之前返回**——候选可能在图外，靠 `inMapBounds` 标示，
  不因出界而报错。

### 4.5 错误消息（逐字，全部包成 `{"error": "..."}`）

```
cannot parse point '{from}'
line missing bearingDeg
circle missing distanceKm
need at least: 1 line with distanceKm, or 2 lines, or line+circle, or 2 circles
observation lines are parallel (bearings equal or opposite)
lines only cross BEHIND {the first|the second} observer, at ({x:F2},{y:F2}) km — a bearing is probably reversed (±180°) or an observer point is wrong; do not retry the same inputs
observation line does not reach the range circle
range circles do not intersect
solution ({x:F2},{y:F2}) km is outside the map — an observation is wrong (bearing reversed, wrong observer grid, or mismatched pairing); re-check the report, do not fire at this
```

坐标格式化助手固定为 `$"({p.x:F2},{p.y:F2})"`。
这些消息是**给 LLM 看的诊断文本**，含明确的"不要用同样输入重试"的指令，
重实现必须原样保留（它们直接影响 agent 的重试行为）。

### 4.6 出界闸门

解出目标后（歧义分支除外）**必须**过 `InMapBounds`；出界即报错，不返回坐标。
理由（注释原意）：宽松包络曾让盲射打到小图真实边缘外 7 km。

---

## 5. 标准结果对象（协议本体）

```json
{ "kmX": <round 3>, "kmY": <round 3>, "grid": "<GridOf 格式>" }
```

- `kmX` / `kmY` 保留 **3 位小数**。
- **只给位置，不给射击诸元**——诸元由 `firing_solution` 工具按实时炮塔原点算。

---

## 6. 地图边界（静态全局状态）

### 6.1 数值常量

| 名称 | 值 | 单位 | 含义 |
|---|---|---|---|
| `EdgeMarginKm` | `0.3` | km | 四边各放宽的边缘余量 |
| 未实测回退包络 | `minX = -1, minY = -1, maxX = 27, maxY = 16` | km | 未测量前的宽松全局包络 |

### 6.2 行为

- `SetMapBoundsKm(minX, minY, maxX, maxY)`：设定本关实测图幅，并置"已实测"标志。
- `ResetMapBounds()`：恢复回退包络，清"已实测"标志。
- `InMapBounds(p)`：`p.x ∈ [minX - 0.3, maxX + 0.3]` 且 `p.y ∈ [minY - 0.3, maxY + 0.3]`，闭区间。
- `MapBoundsText`（供快照文本 `MapExtentKm`）：
  - 已实测：`$"km({minX:F1},{minY:F1})-({maxX:F1},{maxY:F1})"`
  - 未实测：`"未实测(宽松包络)"`（中文逐字）

### 6.3 时序要求

- 场景加载（`OnSceneWasLoaded`）与全局重置（F9 / FullReset）**必须**调用
  `ResetMapBounds()`——边界是进程全局静态量，跨关不清会用上一关的图幅。
- 指挥桌绑定成功后，若图幅实测可用则 `SetMapBoundsKm`，否则 `ResetMapBounds`。
  两条路径都有对应日志（见 §9）。

---

## 7. 计算器（`calc` 工具）

### 7.1 顶层协议

- 入参 JSON 字段：`expression`（string）。缺失/空时回执**逐字** `need expression`。
- 输入按 `;` 分割成多条表达式，逐条 `Trim()`，**跳过空条**。
- 每条成功输出一行：`$"{expr} = {value:G10}"`（`expr` 是 trim 后的原文）。
- 每条失败输出一行：`$"{expr} → error: {message}"`
  （箭头是 **U+2192 `→`**，不是 `->`；文件必须 UTF-8 存盘）。
- 所有行用 `\n` 连接返回。
- 一条有效表达式都没有 → 返回 `empty expression`。
- **一条出错不影响其他条**：逐条独立 try/catch。
- 数值格式化用 `G10`（10 位有效数字）；小数点必须是 `.`（用不变文化格式化，
  不得随系统区域变化）。

### 7.2 词法

- 空白随处可跳过（运算符前后、函数名与括号之间、括号内侧）。
- **数字**：以数字或 `.` 起头；随后连续吞掉 数字 / `.` / `e` / `E`，以及
  **紧跟在 `e`/`E` 之后**的 `+` / `-`。用 `NumberStyles.Float` +
  `InvariantCulture` 解析；失败报 `bad number '{text}'`。
  → 支持 `3e+2`、`.5`；`1e`、`2.3.4` 会走到 `bad number`。
- **标识符**：字母起头，随后字母 / 数字 / `_`。解析后**转小写**
  （`ToLowerInvariant`）→ 函数名与常量名**大小写不敏感**。
- 标识符后跳过空白，若下一字符是 `(` 则按函数调用处理（`sin (30)` 合法），
  否则按常量处理。

### 7.3 语法与优先级（**核心要求**）

从低到高：

```
Expr    := Term  (('+' | '-') Term)*          左结合
Term    := Factor (('*' | '/' | '%') Factor)* 左结合
Factor  := '-' Factor
         | Primary ('^' Factor)?              '^' 右结合
Primary := '(' Expr ')' | 数字 | 标识符 [ '(' 实参表 ')' ]
```

由此必须成立的行为（回归用例）：

| 表达式 | 结果 | 说明 |
|---|---|---|
| `-3^2` | `-9` | `^` **绑得比一元负号紧**，即 `-(3^2)`，符合数学惯例 |
| `2^-3` | `0.125` | 一元负号出现在 `^` 右侧，靠 `ParseFactor` 递归吃下 |
| `2^3^2` | `512` | `^` 右结合 = `2^(3^2)` |
| `10-3-2` | `5` | `+ -` 左结合 |
| `10/2*5` | `25` | `* / %` 左结合 |

- `%` 是 C# 的 double 取余（符号跟被除数）——因此才需要 `mod360` 做方位角归一。
- **没有一元 `+`**（`+5` 会报 `unexpected '+' at position 0`）。
- 除零不报错，产出 `Infinity` / `NaN` 并按 `G10` 打印。

### 7.4 常量

| 名 | 值 |
|---|---|
| `pi` | `Math.PI` |
| `e` | `Math.E` |

未知名 → `unknown constant '{name}'`。

### 7.5 函数表（**协议本体，逐字**）

**炮兵约定贯穿全表：正三角函数吃角度，反三角函数返回角度。** `Deg = π / 180`。

| 函数 | 元数 | 语义 |
|---|---|---|
| `sin(x)` | 1 | `Math.Sin(x * Deg)` — x 为**度** |
| `cos(x)` | 1 | `Math.Cos(x * Deg)` |
| `tan(x)` | 1 | `Math.Tan(x * Deg)` |
| `asin(x)` | 1 | `Math.Asin(x) / Deg` — 返回**度** |
| `acos(x)` | 1 | `Math.Acos(x) / Deg` |
| `atan(x)` | 1 | `Math.Atan(x) / Deg` |
| `atan2(y, x)` | 2 | `Math.Atan2(y, x) / Deg` — **实参顺序 (y, x)**，返回度 |
| `sqrt(x)` | 1 | |
| `abs(x)` | 1 | |
| `ln(x)` | 1 | 自然对数 `Math.Log` |
| `log10(x)` | 1 | |
| `exp(x)` | 1 | |
| `floor(x)` | 1 | |
| `ceil(x)` | 1 | `Math.Ceiling` |
| `round(x)` | 1 | `Math.Round(x)` — **默认银行家舍入（ToEven）**，`round(0.5)=0`、`round(2.5)=2` |
| `pow(a, b)` | 2 | |
| `min(…)` | **≥1，变元** | 全部实参取最小 |
| `max(…)` | **≥1，变元** | 全部实参取最大 |
| `hypot(a, b)` | 2 | `sqrt(a² + b²)` |
| `mod360(x)` | 1 | `((x % 360) + 360) % 360` — 方位角归一到 `[0, 360)` |

注意 `min` / `max` 是唯二**变元**函数，其余严格定元。

### 7.6 调用与错误消息（逐字）

- 实参表：至少一个表达式，逗号分隔；**零参调用不合法**（会报 `unexpected ')' at position N`）。
- 元数不符：`{name} takes 1 argument` / `{name} takes 2 arguments`（单复数区分照抄）。
- 未知函数：`unknown function '{name}'`
- 未知常量：`unknown constant '{name}'`
- 缺字符：`expected '{c}' at position {i}`
- 多余字符：`unexpected '{c}' at position {i}`
- 表达式提前结束：`unexpected end of expression`
- 数字非法：`bad number '{text}'`

位置索引 `i` 是**相对当前这一条表达式（trim 后）的 0 基下标**。

---

## 8. 绘图几何副产物（SolveGeometry）

`solve_target` 除返回 JSON 外，还必须产出一份"作图工作量"供调用方在地图上物理画出：

- `Lines`：`(from, to)` 对的列表。
  - **带 distanceKm 的直接定位线**：在解析阶段就加入 `(观测点, 定位点)`——
    即使后续解算失败，这些线也已在表里（调用方靠 `Solution` 判定是否作图）。
  - **纯方位观测线**：只有在最终解算成功后，才按 `(观测点, 解出的目标点)` 加入
    ——线段终点是交汇点，不是无限长射线。
- `Circles`：`(圆心, 半径 km)` 列表，解析阶段即加入。
- `Solution`：最终目标点；**只有成功路径才置值**（错误与歧义路径均为空）。

调用方契约（`FdoAgent.PlotGeometry`）：仅当 `Solution` 非空才作图，且作图必须
`MainThread.Post`（fire-and-forget，绝不阻塞 agent 线程）。使用的 prefab：

| 内容 | prefab | 端点约定 |
|---|---|---|
| 观测线 | `MapMarkerYellow` | origin=起点，target=终点 |
| 距离圆 | `MapMarkerDiscCompass` | origin=圆心，target=`(圆心x + r, 圆心y)` |
| 解算点 | `MapMarkerRED` | 零长度笔画（origin == target） |

存档标记坐标 == km 帧（已实测标定），故几何量可直接喂给绘图器，无需换算。

---

## 9. 日志格式（逐字）

本模块自身不打日志；但边界设定的两条日志由调用方在绑定时输出，与本模块语义绑定：

```
[AgentBridge] tactical map bound; sheet extent km({MinX:F1},{MinY:F1})-({MaxX:F1},{MaxY:F1})
[AgentBridge] tactical map bound; sheet unmeasured — generous bounds fallback
```

图幅测量不合理时由 MapReader 输出并返回"未测量"：

```
[AgentBridge] map sheet measurement implausible ({width:F1}x{height:F1}km via '{name}') — keeping generous bounds
```

（合理性门槛：宽度 `[5, 40]` km、高度 `[3, 30]` km，超出即判不可信。）

---

## 10. 跨模块契约

### 10.1 本模块对外暴露

| 接口 | 消费方 | 用途 |
|---|---|---|
| `ParsePoint(spec, turretKm)` | fire 的 `target`、adjust 的 `target`、`set_assumed_turret_position` 的 `position`、`firing_solution` 的 `target`、`distance_between` / `entities_near` 的端点解析 | 网格/`turret`/`kmX,kmY` → km |
| `GridOf(p)` | 弹着播报（`ImpactReader`）、结果对象、候选对象 | km → 网格显示串 |
| `InMapBounds(p)` | fire 出界闸门、adjust 出界闸门、`SetDeclaredTurret` 校准闸门、`firing_solution` 的 `inMapBounds` 字段、`entities_near` 结果 | 出界防呆 |
| `SetMapBoundsKm` / `ResetMapBounds` | 指挥桌绑定、场景加载、FullReset | 每关设定/清除图幅 |
| `MapBoundsText` | 快照字段 `MapExtentKm`，快照文本行"本关地图实测范围: …" | 给 LLM 的规划边界 |
| `GridToKm(args, turretKm)` | 工具 `grid_to_km` | |
| `SolveTarget(args, turretKm[, out geometry])` | 工具 `solve_target` | 两个重载：无 geometry 的版本内部丢弃几何 |
| `SolveGeometry` | `FdoAgent.PlotGeometry` → `MapDrawer` | 物理作图 |
| `Calculator.Evaluate(expr)` | 工具 `calc` | |

### 10.2 本模块依赖

- **无**。仅 BCL（`Math`、`Regex`、`System.Text.Json`、`double.TryParse`）。
- `turretKm` 由调用方从棋子 `Player Turret Piece` 的 localPosition 换算后传入；
  本模块不读游戏状态。

### 10.3 相关但**不属于**本模块的计算（重实现时注意别混入）

- 射击诸元（方位/距离）由 `firing_solution`、`distance_between` 在调用方计算：
  `bearing = atan2(dx, dy) * 180/π`，负值 +360；`dist = hypot(dx, dy)`。
- 仰角公式 `仰角 = 距离km × 12 / 装药数`（60° 封顶）属 FCS 侧，不在本模块。
- 偏移量 `offsetKmX/offsetKmY` 的 ±0.5 km 上限校验属 fire 模块。

---

## 11. 不变量与防御性规则

1. **纯函数 + 线程安全**：本模块无 Unity 调用，可在 agent 后台线程直接调用，
   **不得**要求 `MainThread.Run`。这是它能在工具执行路径上零延迟运行的前提。
2. **静态边界是唯一可变全局态**，跨场景残留会造成"上一关的图幅"，
   场景加载与 F9 全重置必须清。若未来考虑多实例/并发，需注意它是进程级共享。
3. **任何入口都不得抛异常穿透到调用方**：`ParsePoint` 失败返回空，
   `GridToKm` / `SolveTarget` 失败返回 `{"error": …}` JSON。
   **旧实现在此有缺口**（见 §12 开放问题 2），重实现必须补齐：JSON 里字段类型不对
   （如 `distanceKm` 给了字符串）也必须变成结构化 error，而不是异常。
4. **`Calculator.Evaluate` 永不抛异常**：每条表达式独立 try/catch，
   异常转成 `→ error:` 行。
5. **出界即拒**：解算结果出界必须报错而非返回坐标——出界解意味着观测有误
   （方位反了 180°、观测员网格抄错、配对错线），继续开火会砸到图外。
6. **歧义不擅自裁决**：圆圆双解且无 `near` 时必须回两个候选，由模型按情报选。
7. **背后解即错解**：两线交汇的 `t`/`s` 必须非负；线圆的根必须 `t ≥ 0`。
   永远不返回观测员身后的解。
8. **编码陷阱**：本模块源文件含中文字面量（歧义 `note`、`未实测(宽松包络)`）
   与 U+2192 `→`。文件必须 UTF-8 保存；**绝不用 PowerShell 的
   `Get-Content` / `-replace` / `Set-Content` 修改**（中文 Windows 会按 GBK 误读
   UTF-8 再回写，全文乱码）。
9. **文化不变性**：所有数字解析用 `CultureInfo.InvariantCulture`；
   所有数字格式化的小数点必须是 `.`（不随中文系统区域漂移）。
10. **`+0.05` 格心偏移不可省**：网格解析必须落在 0.1 km 子格的**中心**，
    不是子格左下角。
11. **不得在本模块里应用 `3.8164` / `(10.016, 5.235)`**——重复应用会造成
    坐标被换算两次，是历史上最容易犯的错。

---

## 12. 逐字保留数据块

以下是给 LLM 的自然语言 prompt/工具描述，**不重写、原样搬运**：

| 内容 | 位置 |
|---|---|
| 网格方向铁律段（"字母A→Z是横轴…方向搞反会把整轮侦察/火力砸到相反半区"） | `C:\Users\stevenli\Codes\IronNestAgentBridge\agent\FdoAgent.cs`:114-117 |
| 定位计算工具清单段（grid_to_km / solve_target / calc / distance_between 的使用口径，含"严禁手算三角"） | `agent\FdoAgent.cs`:118-130 |
| 工具 `grid_to_km` 的 JSON schema 与中文描述 | `agent\FdoAgent.cs`:242-253 |
| 工具 `calc` 的 JSON schema 与中文描述（含完整函数表口径与示例串） | `agent\FdoAgent.cs`:364-380 |
| 工具 `solve_target` 的 JSON schema 与中文描述（含 line/circle/near 的电文语义映射） | `agent\FdoAgent.cs`:416-451 |
| 盲射精度认知段（"网格±0.05km、方位角±0.5°"，与本模块的格心/角度约定直接相关） | `agent\FdoAgent.cs`:154-157 |

---

## 13. 回归用例清单（重实现自测）

**GridMath**

| 输入 | 期望 |
|---|---|
| `"G6 5:3"` | `(6.55, 5.35)`；`GridOf` 回环得 `"G6 5:3"` |
| `"a1 0:0"` | `(0.05, 0.05)` |
| `" H 5  0 : 9 "` | `(7.05, 4.95)` |
| `"turret"` / `"TURRET"` | 原样返回 `turretKm` |
| `"7.35,1.45"` | `(7.35, 1.45)` |
| `"G6"` / `"7.35"` / `"a,b,c"` | 解析失败 |
| `GridOf((-0.5, 3.2))` | 列字母为 `"#"`，不抛异常 |
| 两线：`turret` 0° 与 (5,0) 270° | 交汇于炮塔正北与该点正西的交点 |
| 两线方位相同或相差 180° | `parallel` 错误 |
| 两线只在背后相交 | `BEHIND the first/second observer` 错误 |
| 相切的两圆 | 单解，不进歧义分支 |
| 相交两圆 + 无 `near` | `ambiguous: true` + 2 候选 |
| 相交两圆 + 有 `near` | 单一结果，取近者 |
| 解落在包络外 | `outside the map` 错误 |

**Calculator**

| 输入 | 期望 |
|---|---|
| `-3^2` | `-9` |
| `2^-3` | `0.125` |
| `2^3^2` | `512` |
| `sin(30)` | `0.5` |
| `atan2(3.2,4.1)` | `≈ 37.97`（度） |
| `mod360(275+120)` | `35` |
| `mod360(-30)` | `330` |
| `hypot(3,4)` | `5` |
| `min(1,2,3,4)` | `1`（变元） |
| `sin(1,2)` | `sin takes 1 argument` |
| `foo(1)` | `unknown function 'foo'` |
| `x` | `unknown constant 'x'` |
| `3 + ` | `unexpected end of expression` |
| `(1+2` | `expected ')' at position 4` |
| `1+2) ` | `unexpected ')' at position 3` |
| `hypot(3,4); sin(30); bogus` | 三行，前两行 `=`，第三行 `→ error:` |
| `";;;"` | `empty expression` |
