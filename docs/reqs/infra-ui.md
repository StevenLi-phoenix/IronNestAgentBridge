# 模块 infra-ui —— F10 控制面板 / 主线程调度器 / 构建与编译期基础设施

覆盖旧实现文件：`Ui/AgentWindow.cs`、`MainThread.cs`、`NullablePolyfill.cs`、`IronNestAgentBridge.csproj`（并附 `tools/Build.ps1` 的部署约束）。

本模块提供三件事：
1. 游戏内 IMGUI 状态面板（唯一的人机界面，F10 开关）；
2. 后台线程 → Unity 主线程的工作项调度器（所有 Il2Cpp/游戏状态访问的唯一合法通道）；
3. 编译期基础设施：目标框架、引用集、输出路径、Il2Cpp 可空属性 polyfill。

---

## 1. F10 控制面板（AgentWindow）

### 1.1 渲染技术边界（硬约束，不可谈判）

- 本游戏的 IL2CPP 构建**裁剪了整个 `GUILayout` 家族**（`GUILayout.Window` / `GUILayout.BeginArea` 等一律抛 `"Method unstripping failed"`）。面板**只允许**使用 `GUI.Box` + `GUI.Label` + 手工计算的 `Rect` 坐标绘制。禁止任何布局系统、滚动视图（`GUI.BeginScrollView` 同样不可用）、窗口拖拽。
- `GUI.Button` **可能**同样被裁剪，因此必须**运行时探测**：所有按钮绘制经统一包装函数发出，第一次抛异常即置内部标志 `_buttonsBroken = true`，此后**永不再调用** `GUI.Button`，包装函数一律返回 `false`（表示"未点击"）。探测异常必须被吞掉，不得冒泡到 `OnGUI`。
- 一旦 `_buttonsBroken`：整行按钮不再绘制，并在正文末尾追加一行灰色提示，文本逐字为：
  `按钮被游戏裁剪: F11=LLM开关 F9=全重置`
- 结论性要求：**热键必须始终可用且是权威操作路径**，按钮只是便利；面板功能不得只经按钮暴露。

### 1.2 可见性与绘制门槛

- 面板有一个公开可变的 `Visible` 布尔，**默认 `true`**；不持久化（每次进程启动都为 true）。
- `Visible == false` 时 `Draw` 立即返回，不做任何计算。
- 上层（mod 的 `OnGUI`）只在**三条件同时满足**时才调用 `Draw`：agent 实例已创建、地图已绑定（`MapReader.IsBound`）、且**不在过场动画中**（`CinematicActive == false`）。语义：未绑定场景/播 CG 时 HUD 必须完全隐身（与 FCS HUD 行为一致）。
- `Draw` 每个 IMGUI 事件都会被调用（一帧内可能多次），因此**必须是无副作用的纯绘制**：唯一允许的副作用是按钮点击触发的两个回调（见 1.7）与 `_buttonsBroken` 探测标志。不得在其中读游戏状态以外的东西、不得写日志、不得触发 IO。

### 1.3 几何常量（逐字保留的数值与单位，单位均为 IMGUI 像素）

| 常量 | 值 | 含义 |
|---|---|---|
| `Y` | `40f` | 面板顶边 y |
| `W` | `470f` | 面板宽 |
| `X` | `Screen.width - W - 20f` | **每帧重算**，右上角贴边留 20px；刻意避开左上角的 FCS HUD |
| `LineH` | `19f` | 行高（行间步进） |
| `WrapChars` | `52` | 折行/截断的**字符数**（非像素） |
| `buttonRowH` | `26f` | 按钮行占高 |
| `toolBudget` | `3` | 最近工具调用显示条数 |
| `logBudget` | `12` | 日志显示条数 |
| 屏高上限 | `Screen.height * 0.8f` | 面板最大高度 |

派生量（必须逐字复现算式）：
- `maxTotalLines = Math.Max(14, (int)((maxHeight - 30f - buttonRowH - 10f) / LineH))`
- `reservedTail = toolBudget + 1 + logBudget + 1`（= 17：工具行 + 日志表头 + 日志 + 提示行）
- `textBudget = Math.Max(8, maxTotalLines - lines.Count - reservedTail - 1)`，其中 `lines.Count` 取的是**加入流式/决策文本之前**已组好的固定头部行数。
- 面板高度 `height = Math.Min(30f + buttonRowH + lines.Count * LineH + 10f, maxHeight)`（`lines.Count` 为最终裁剪后的行数）。
- 面板矩形 `Rect(X, Y, W, height)`。

### 1.4 绘制时序（两趟：先组行，再定高）

必须**先把所有正文行组装成 (文本, 颜色?) 列表，再据行数计算箱体高度**——面板高度随内容自适应，不得先画框后填字。

绘制顺序：
1. `GUI.Box(box, "IronNest Agent Bridge  [F10]")` —— 标题逐字（`Bridge` 与 `[F10]` 之间是**两个空格**）。
2. 光标 `y = box.y + 24f`。
3. 按钮行（仅在 `!_buttonsBroken` 时绘制），随后无论是否绘制**都要** `y += buttonRowH`（即按钮被裁剪时该行留白，正文起始位置不变）。
4. 逐行 `GUI.Label(new Rect(box.x + 10f, y, W - 20f, LineH + 2f), text)`，每行后 `y += LineH`。
5. 每行绘制前设 `GUI.color = 该行颜色 ?? Color.white`；整个循环前保存 `GUI.color` 原值，循环后**必须还原**（否则会污染游戏自身 UI 的着色）。

### 1.5 正文内容与顺序（逐字文本/格式）

按下列顺序追加行：

1. **状态点行**：`stateText + "  " + AgentConfig.Model`（两个空格分隔），颜色随状态：
   - `AgentState.Running` → `"● RUNNING"`，`Color.green`
   - `AgentState.Paused` → `"● PAUSED"`，`Color.yellow`
   - `AgentState.Stopping` → `"● STOPPING"`，`new Color(1f, 0.55f, 0f)`（橙）
   - 其余（`Stopped`）→ `"● STOPPED"`，`Color.red`
2. `$"状态: {agent.Status}"`（默认白）
3. `UsageMeter.Summary`（该串由 agent 模块提供，面板原样显示，不重排版）
4. `$"context: {UsageMeter.LastPromptTokens:N0} tokens"` —— `N0` 千分位、无小数。
5. **FCS 摘要**：把 `mod.LastFcsSummary` 按 `'\n'` 切分，**跳过空串**，每段一行（白）。该串目前形如
   `FCS: pending={n} done={n} fail={n}`，可选追加 `\nT1(左): {desc}`、`\nT2(右): {desc}`。面板不解析它，只做切分显示。
6. **思考/决策段**（二选一，流式优先）：
   - 若 `agent.IsStreaming`：先加 `"—— 思考中 ▌ ——"`（`Color.cyan`），再把 `agent.StreamingText` 经折行后按 `textBudget` 行**取尾部**（`fromEnd: true`，即始终看最新输出），全部 `Color.cyan`。
   - 否则若 `agent.LastReason.Length > 0`：先加 `"—— 最新决策 ——"`（`Color.yellow`），再折行后**取头部**，行数上限 `Math.Min(textBudget, 14)`，全部 `Color.yellow`。
7. **最近工具调用**：取 `agent.RecentToolCalls()` 的**最后 `toolBudget`(3) 条**，每条渲染为
   `"🔧 " + (长度 > 52 ? 前52字符 + "…" : 原文)`（白）。
8. **日志**：取 `agent.LogSnapshot()`，若非空先加表头 `"—— 日志 ——"`（白），再取**最后 `logBudget`(12) 条**，每条按 `WrapChars + 10 = 62` 字符截断并追加 `"…"`（不折行，只截断）。
9. **按钮裁剪提示行**（见 1.1），灰色 `Color.gray`。
10. 最后若 `lines.Count > maxTotalLines`，从索引 `maxTotalLines` 起**删除尾部多余行**（硬截断，宁可丢日志也不越过 80% 屏高）。

### 1.6 折行规则（Wrap）

- 入参：文本、最大行数、`fromEnd` 标志（默认 `false`）。
- `null` 文本按空串处理；先删除所有 `'\r'`，再按 `'\n'` 切分（即 CRLF/LF 归一）。
- 空行**保留为空串行**（不吞行，保持段落间距）。
- 非空行按**固定 52 字符硬切**，不做单词/标点边界处理，也不做 CJK 宽度换算（52 个 char，中英混排等宽计数）。
- 若总行数 ≤ 最大行数，全部返回；否则 `fromEnd` 为真取**末尾** N 行，为假取**开头** N 行。

### 1.7 交互（按钮 + 热键）

按钮行（仅在按钮可用时）：
- 左按钮 `Rect(box.x + 10f, y, 110f, 22f)`，标签随 `agent.IsRunning`：运行中显示 `"停止 LLM"`，否则 `"启动 LLM"`；点击 → 调用 `mod.ToggleLlmControl()`。
- 右按钮 `Rect(box.x + 126f, y, 90f, 22f)`，标签 `"全重置"`；点击 → 调用 `mod.FullReset("panel button")`（reason 字符串逐字）。

热键（在 `OnUpdate` 中经 `UnityEngine.InputSystem` 的 `Keyboard.current` 读取 `wasPressedThisFrame`，整块必须包在 `try/catch` 里静默吞异常，因为 `Keyboard.current` 在某些场景为 null 或 Il2Cpp 侧抛错）：
- **F10** → 翻转面板 `Visible`
- **F11** → `ToggleLlmControl()`（LLM 总控）
- **F9** → `FullReset("F9")`（与 FCS 的计划重置同键联动，语义刻意对齐）

`ToggleLlmControl` 的语义（面板依赖的契约）：翻转 `AgentConfig.LlmControl`（写入并 `MelonPreferences.Save()`），据新值启动或停止 agent 线程，并打日志
`[AgentBridge] LLM control ON` / `[AgentBridge] LLM control OFF`。

`FullReset(reason)` 的语义（面板依赖的契约）：先打 `[AgentBridge] full reset ({reason})`，写 transaction log `reset` 条，停 agent、清 agent 日志与对话、清事件队列与各类簿记、解绑地图并安排 1 秒后重绑。

---

## 2. 主线程调度器（MainThread）

### 2.1 存在理由（不变量）

- **游戏/Il2Cpp 状态只能在 Unity 主线程的 `OnUpdate` 泵里访问。** HTTP 监听线程、agent 后台线程一律不得直接触碰 Il2Cpp 对象、Unity API 或反射到的 FCS 对象。任何跨线程访问必须经本调度器。
- 队列必须是**线程安全的 FIFO**（`ConcurrentQueue<Action>`），生产者多线程、消费者单线程。

### 2.2 API 契约

- `Task<T> Run<T>(Func<T> func, int timeoutMs = 10_000)`
  - 入队一个闭包，在主线程执行 `func()`，结果经 `TaskCompletionSource<T>` 回传；`func` 抛出的异常必须**捕获并作为任务异常**回传给调用方（绝不能在 `Pump` 里冒泡）。
  - TCS 必须以 `TaskCreationOptions.RunContinuationsAsynchronously` 创建 —— 续体不得在主线程上同步跑。
  - 超时：`timeoutMs` 到点后以 `TimeoutException` 完成任务，消息**逐字**为：
    `main-thread call not serviced within {timeoutMs}ms (game unfocused or scene loading?)`
  - 完成语义用 `TrySet*`：先到者胜（超时后再返回结果不得抛"任务已完成"异常，反之亦然）。
- `Task Run(Action action, int timeoutMs = 10_000)` —— 用 `Run<object?>` 实现的重载，等价语义。
- `void Post(Action action)` —— **即发即忘**：入队但不等待，执行时的异常**整块吞掉**（不打日志）。用途仅限装饰性/无关正确性的工作（地图作图等），要求是**绝不阻塞 agent 循环**；游戏暂停时它只是延后到循环恢复才跑。
- `void Pump()` —— 由 mod 的 `OnUpdate` **每帧调用一次**，把队列**排空**（`while TryDequeue` 直到空），不设每帧数量上限、不设时间片。因两种入队路径的闭包内部都自带 try/catch，`Pump` 本身必须是**永不抛异常**的。

### 2.3 调用方约定与已知超时值

- HTTP 处理线程与 agent 线程通过 `MainThread.Run(...).GetAwaiter().GetResult()` 同步等待。
- 现存超时取值：HTTP 端点一律用默认 `10_000` ms；agent 侧读取类（`ReadTurretLocal`、`FindVisibleEntity`、`PullSignalHorn`）用 `10_000`，动作/快照类（`QueueFireMission`、`AdjustFireMission`、`CancelPendingFcsTask`、`SetDeclaredTurret`、`RequestCard`、`BuildSnapshot`）用 `15_000`。
- **致命禁忌**：不得从主线程自身调用 `Run(...)` 并同步等待——那会阻塞 `Pump`，工作项永不执行，直到超时才解开（死锁 10~15 秒）。
- **超时不取消工作项**：超时只是让调用方提前拿到异常，闭包仍留在队列里，将来某帧照样执行（副作用会迟到发生）。重实现若要改变该语义，必须显式设计取消。
- 队列在场景切换、`FullReset` 时**不被清空**——遗留工作项会在重绑后的世界里执行。

---

## 3. Il2Cpp 可空属性 polyfill（NullablePolyfill）

- 背景：`Il2Cppmscorlib` 遮蔽真正的 corelib，隐藏了编译器为 `#nullable enable` 生成的属性类型，导致项目开 `Nullable` 后编译失败。必须在项目内**重新声明**这些属性类型（与 IronNestFCS 的 `Shared/NullablePolyfill.cs` 同款做法）。
- 要求：在 `namespace System.Runtime.CompilerServices` 下声明三个 `internal sealed class`，全文件 `#pragma warning disable`：
  - `NullableAttribute` —— `AttributeUsage(Class | Event | Field | GenericParameter | Parameter | Property | ReturnValue, AllowMultiple = false, Inherited = false)`；成员 `public readonly byte[] NullableFlags`；两个构造 `(byte flag)` 与 `(byte[] flags)`。
  - `NullableContextAttribute` —— `AttributeUsage(Class | Delegate | Interface | Method | Struct, AllowMultiple = false, Inherited = false)`；成员 `public readonly byte Flag`；构造 `(byte flag)`。
  - `NullablePublicOnlyAttribute` —— `AttributeUsage(Module, AllowMultiple = false, Inherited = false)`；成员 `public readonly bool IncludesInternals`；构造 `(bool includesInternals)`。
- 类型名、成员名、`AttributeTargets` 组合必须**逐字一致**（编译器按名字查找并发射这些属性，改名即失效）。
- 该文件是纯编译期设施，运行时无行为；重实现原样搬运即可（见「逐字保留数据块」）。

---

## 4. 构建配置（csproj / Build.ps1）

### 4.1 项目属性（逐字）

- `TargetFramework` = `net6.0`（MelonLoader net6 运行时）
- `ImplicitUsings` = `enable` —— 代码依赖隐式 using（`System`、`System.Linq`、`System.Collections.Generic`、`System.Threading.Tasks` 等，`TakeLast`/`Math`/`Exception` 均由此可见）
- `Nullable` = `enable`（故需要 §3 的 polyfill）
- `LangVersion` = `latest`（文件级 namespace、`switch` 表达式、`{ } kb` 模式、`t[..N]` range 均在用）
- `AppendTargetFrameworkToOutputPath` = `false` —— 产物必须直接落在 `Mods\` 根，不能有 `net6.0\` 子目录
- `GameDir` = `D:\SteamLibrary\steamapps\common\Iron Nest Heavy Turret Simulator`（默认值，可 `-p:GameDir=...` 覆盖）
- `OutputPath` = `$(GameDir)\Mods\`
- `AssemblyName` = `IronNestAgentBridge`，`RootNamespace` = `IronNestAgentBridge`
- 排除项：`<Compile Remove="agent\.venv\**" />` 与 `<None Remove="agent\.venv\**" />`（仓库里有 Python 侧 `agent\agent.py` 及其虚拟环境，绝不能被 SDK 通配进编译）

### 4.2 程序集引用（全部 `<Private>false</Private>`，不复制到输出）

来自 `$(GameDir)\MelonLoader\net6\`：`MelonLoader.dll`、`Il2CppInterop.Runtime.dll`、`0Harmony.dll`。
来自 `$(GameDir)\MelonLoader\Il2CppAssemblies\`：`Il2Cppmscorlib.dll`、`Assembly-CSharp.dll`、`UnityEngine.CoreModule.dll`、`UnityEngine.PhysicsModule.dll`、`UnityEngine.UI.dll`、`UnityEngine.IMGUIModule.dll`、`UnityEngine.UIModule.dll`、`Unity.TextMeshPro.dll`、`Unity.InputSystem.dll`（该条的 `Reference Include` 名写作 `UnityEngine.InputSystem`，与 HintPath 文件名 `Unity.InputSystem.dll` 不一致，属既有事实）。

界面相关的最小依赖：`UnityEngine.CoreModule`（`Screen`/`Color`/`Rect`）、`UnityEngine.IMGUIModule`（`GUI`）、`Unity.InputSystem`（热键）。

### 4.3 程序集级 Melon 属性（逐字）

```
[assembly: MelonInfo(typeof(IronNestAgentBridge.AgentBridgeMod), "IronNest Agent Bridge", "0.1.0", "stevenli")]
[assembly: MelonGame()]
```
`MelonGame()` 无参 = 不限定游戏。Melon 显示名 `IronNest Agent Bridge` 是 FCS 侧反射查找本 mod 时的键之一，改名有跨模块影响。

### 4.4 构建/部署流程规则

- `tools\Build.ps1`（参数 `-Configuration`，默认 `Release`）在构建前用
  `Get-Process "Iron Nest Heavy Turret Simulator"` 检测游戏进程；**进程存在即拒绝构建并 `exit 1`**，错误文本逐字：
  `game is running (pid $($game.Id)) - close it first, Mods\IronNestAgentBridge.dll is locked`
  理由：已加载的 DLL 被锁，拷贝会失败并留下过期 mod。
- 构建命令 `dotnet build $project -c $Configuration -m:10`（`-m:10` 限并行度），脚本以 `$LASTEXITCODE` 退出。
- 游戏运行中若必须验证编译，使用 `-p:OutputPath=bin\staging\` 构建到暂存目录，关游戏后再拷入 `Mods\`。
- **编码陷阱（事故记录，必须写进重实现须知）**：含中文的源文件**绝不可**用 PowerShell 的 `Get-Content` / `-replace` / `Set-Content` 修改——中文 Windows 会以 GBK 误读 UTF-8 再回写，全文乱码。只用 UTF-8 安全的编辑方式。所有源文件必须是 **UTF-8**（面板文本含中文、`●`、`▌`、`🔧`、`——` 等非 ASCII 字符，编码错了面板会显示乱码）。
- 配置文件 `UserData\MelonPreferences.cfg` 的 `[AgentBridge]` 段**绝不可在游戏运行中手改**——游戏任一次 `MelonPreferences.Save()` 会按内存值整文件重写，手改必被清。运行期改开关只能走热键/面板（F11）。

---

## 5. 跨模块契约

### 5.1 本模块向外暴露

| 接口 | 消费者 | 语义 |
|---|---|---|
| `MainThread.Run<T>(Func<T>, timeoutMs)` / `Run(Action, timeoutMs)` | HTTP 服务器（BridgeServer）、agent 工具层 | 同步等待的主线程调用，异常/超时经 Task 回传 |
| `MainThread.Post(Action)` | agent 的作图等装饰性动作 | 即发即忘、吞异常、绝不阻塞 |
| `MainThread.Pump()` | mod 的 `OnUpdate`（每帧一次，且应在其它逻辑之前） | 排空队列 |
| `AgentWindow.Visible`（可读写字段） | mod 的 F10 热键处理 | 面板显隐 |
| `AgentWindow.Draw(FdoAgent, AgentBridgeMod)` | mod 的 `OnGUI` | 全量绘制 |
| `System.Runtime.CompilerServices` 三个属性类型 | 全项目（编译期） | 使 `#nullable enable` 可编译 |
| csproj 的 `GameDir`/`OutputPath` | 构建脚本、开发者 | 直接部署进 `Mods\` |

### 5.2 本模块依赖于外部

来自 **agent 模块**（`FdoAgent`）：`IsRunning`、`State`（枚举 `AgentState { Stopped, Running, Paused, Stopping }`）、`Status`、`LastReason`、`IsStreaming`、`StreamingText`、`RecentToolCalls()`（返回 `List<string>`，实现内部加锁快照）、`LogSnapshot()`（`IReadOnlyList<string>` 快照）。**这些成员会被 UI 线程每帧读取，必须是线程安全的快照读**（返回副本、字段读取原子）。

来自 **agent 配置**：`AgentConfig.Model`（MelonPreferences 键 `Model`，默认 `deepseek-v4-flash`）、`AgentConfig.LlmControl`（键 `LlmControl`，默认 `false`，**每次启动强制置回 false**，F11/面板才开）。

来自 **usage 计量**：`UsageMeter.Summary`（成品串）、`UsageMeter.LastPromptTokens`（long）。

来自 **mod 主体**（`AgentBridgeMod`）：`LastFcsSummary`（每 2 秒刷新的 FCS 摘要串，可含 `\n`）、`ToggleLlmControl()`、`FullReset(string reason)`、静态 `CinematicActive`（`volatile bool`，面板绘制门槛）、`MapReader.IsBound`。

来自 Unity：`Screen.width` / `Screen.height`（每帧读，随分辨率变化自适应）、`GUI.Box/Label/Button`、`Color`、`Rect`、`Keyboard.current`（InputSystem）。

---

## 6. 不变量与防御性规则（单列）

1. **主线程唯一性**：任何 Il2Cpp 对象、Unity API、反射得到的 FCS 实例，只能在 `Pump` 内（即 `OnUpdate` 主线程栈上）触碰。HTTP/agent 线程直接访问 = 崩溃或静默数据损坏。
2. **不得从主线程同步等待 `MainThread.Run`**（自死锁）。
3. `Pump` 必须永不抛异常：两条入队路径都在闭包内部自带 try/catch。
4. `Post` 的异常静默吞掉是**刻意**的（装饰性工作失败不得影响作战）；`Run` 的异常必须原样传回调用方。
5. **GUI 端一律 try/catch 保护 Il2Cpp 裁剪**：`GUI.Button` 探测失败后永久禁用；热键读取整块 try/catch；`OnGUI` 不得让异常逃逸（每帧异常会刷爆 MelonLoader 日志并可能拖垮渲染）。
6. **禁止 `GUILayout` 全家**（含 `GUILayout.Window`/`BeginArea`/滚动视图）——该游戏 IL2CPP 已裁剪，调用即 `Method unstripping failed`。
7. **面板高度 ≤ 80% 屏高**，超出部分硬截断；行预算的分配次序是"固定头部 → 保留尾部（工具/日志/提示）→ 剩余给思考/决策文本"。
8. **`GUI.color` 必须保存并还原**，否则污染游戏自身 UI。
9. **绘制无副作用**：`OnGUI` 一帧多次调用，任何计数/状态推进都不得放在 `Draw` 里。
10. **面板对 agent 状态只读**，唯一的写入路径是两个按钮回调（与热键同函数）。
11. **场景未绑定 / 过场动画期间不绘制**（也不做组行计算）。
12. **源码编码必须 UTF-8**；禁止用 PowerShell 文本管道改含中文的源文件。
13. **游戏运行中不构建到 `Mods\`**（DLL 被锁）；不手改运行中的 `MelonPreferences.cfg`。
14. **`AppendTargetFrameworkToOutputPath=false` 不可省**，否则产物落进 `Mods\net6.0\`，MelonLoader 不加载。
15. 引用一律 `Private=false`：绝不把 Unity/MelonLoader 程序集复制进 `Mods\`（会与游戏自带版本冲突）。
16. `agent\.venv\**` 必须排除出编译与 None 项。

---

## 7. 逐字保留数据块

本模块不含大段自然语言数据（无 SystemPrompt / 情报表 / 学说文本）。以下为"原样搬运"的结构性块：

- `C:/Users/stevenli/Codes/IronNestAgentBridge/NullablePolyfill.cs:1-36` —— 三个可空属性 polyfill 类型的完整声明，整文件照搬（类型名/成员名/AttributeTargets 组合具有编译器语义）。
- `C:/Users/stevenli/Codes/IronNestAgentBridge/IronNestAgentBridge.csproj:20-69` —— `<ItemGroup>` 引用清单（12 条 Reference 的 Include 名与 HintPath），照搬。

---

## 8. 未决问题（openQuestions）

1. `AgentWindow` 的类注释声称热键为「F10 面板、F11 LLM 控制、**F12 优先级队列**、F9 全重置」，但 `AgentBridgeMod.OnUpdate` 中**没有任何 F12 处理**（内部暂存优先队列已整体移除）。F12 是否应彻底删除，还是要在新实现里恢复某种功能？
2. `LastFcsSummary` 里左右炮标注为 `T1(左)` / `T2(右)`，与项目知识库现行体制（**T9=左炮、T10=右炮**为 FCS 自动维护的炮位标记，T1–T8 归玩家）冲突。面板上的这两行标签是否应改为 T9/T10？（面板只做透传，实际字符串在 mod 主体里拼。）
3. `textBudget` 用的是**追加思考/决策文本前**的 `lines.Count`，而 `reservedTail` 按满额（3 工具 + 12 日志）预留；当工具/日志不足额时这部分空间被白白浪费，末尾还有一次 `maxTotalLines` 硬截断。是否要改成先组尾部、再把剩余空间全给正文？属设计意图不明。
4. 超时后队列中的工作项仍会执行（副作用迟到落地），例如一条超时的 `QueueFireMission` 可能在 20 秒后真的排进了 FCS 而调用方已按失败处理。这是有意接受的风险还是遗漏？重实现是否应引入取消/作废标记？
5. `MainThread.Run` 里的 `CancellationTokenSource` 从不 Dispose，且注册的回调在成功路径上也会在定时到点时跑一次（`TrySetException` 变成空操作）。是否需要在完成时取消计时器（每次调用一个 timer，长会话下的开销）？
6. 队列在 `FullReset` / 场景切换时不清空——遗留闭包会在新绑定的世界里执行。是否应在重置时丢弃在途工作项？
7. 状态点用 `agent.State`，而按钮标签用 `agent.IsRunning`（线程存活）。两者可能短暂不一致（如 `Stopping` 时线程仍活 → 显示"● STOPPING"但按钮写"停止 LLM"）。是否需要统一到一个来源？
8. 流式文本取尾、决策文本取头且额外封顶 14 行——取向不同是有意（看最新 vs 看结论开头）还是历史偶然？决策的 14 行硬上限依据不明。
9. `mod.LastFcsSummary ?? ""` 对一个非空属性做了空合并；同理 `Wrap` 里 `text ?? ""`。属防御性冗余还是曾有 null 路径？
10. 面板 `Visible` 不持久化、`_buttonsBroken` 也不持久化（每次进程重新探测）。按钮探测结果是否值得缓存到配置？（探测一次即定，影响很小。）
11. csproj 中 `Reference Include="UnityEngine.InputSystem"` 指向 `Unity.InputSystem.dll`，命名不一致；是刻意（代码里 `using UnityEngine.InputSystem`）还是应对齐为 `Unity.InputSystem`？
12. `Build.ps1` 只实现了"游戏在跑就拒绝"，没有知识库里要求的 `-p:OutputPath=bin\staging\` 暂存构建通道。是否应把 staging 流程内建成脚本参数（如 `-Staging`）？
13. `GameDir` 的默认值是开发者机器上的绝对路径（`D:\SteamLibrary\...`）。发行/他人构建时是否应改为必填属性或从 Steam 库自动探测？
14. `WrapChars = 52` 是字符数而非像素宽，中英混排时 CJK 实际占宽约两倍，52 个中文字远超 `W - 20 = 450px` 的标签宽度而被 IMGUI 裁掉。当前是否存在中文行右侧被截断的显示缺陷（未见任何按字宽区分的处理）？
