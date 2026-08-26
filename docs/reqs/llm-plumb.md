# 模块 llm-plumb — LLM 客户端 / 配置 / 用量计量 / 事务日志

覆盖范围：与 LLM 服务商之间的全部管道（OpenAI 兼容流式协议、函数调用多轮循环、工具轮上限与
强制收尾、前缀缓存保持约定）、MelonPreferences 配置键、会话用量与费用计量、落盘 JSONL 事务日志。

不覆盖：agent 决策循环、快照拼装、工具语义与实现、系统提示词内容（属 agent-loop / tools 模块）。

---

## 1. 协议：OpenAI 兼容流式聊天

### 1.1 请求

- 端点必须是 `{BaseUrl}/chat/completions`，方法 `POST`。`BaseUrl` 取自配置并**必须去掉尾部
  斜杠**（`TrimEnd('/')`），使得配置写 `https://api.deepseek.com/` 与不带斜杠等价。
- 鉴权头：`Authorization: Bearer {ApiKey}`。必须以**不校验**的方式添加（`TryAddWithoutValidation`）
  —— 某些 key 含非 token 合法字符时标准校验会抛异常。
- 请求体 `Content-Type: application/json`，正文以 **UTF-8** 编码。
- 请求体 JSON 字段（字段名逐字）：
  - `model` = 配置 `Model`
  - `messages` = 调用方持有的完整消息数组（见 §1.5 缓存约定）
  - `max_tokens` = 配置 `MaxTokens`
  - `temperature` = **0.3**（硬编码常量，不来自配置）
  - `stream` = `true`
  - `stream_options` = `{ "include_usage": true }`
  - `tools` = 仅当本轮传入了工具 JSON 时才出现；其值为把工具 JSON 文本**原样解析后嵌入**的
    JSON 数组（不得二次转义成字符串）。工具 JSON 为 `null` 时该字段必须整体缺席，而不是
    `"tools": null` 或 `[]`。
- 必须用 `HttpCompletionOption.ResponseHeadersRead` 等价语义发起：响应头一到就返回，正文按流读，
  绝不整体缓冲。
- HTTP 客户端为**进程内单例**，超时 **300 秒**。单例是刚需：每轮新建客户端会耗尽 socket 并丢掉
  连接复用，进而破坏服务商侧的前缀缓存亲和性。

### 1.2 失败响应

- 响应状态码非 2xx 时，读取完整正文，抛出异常，消息格式**逐字**为：
  `LLM API {statusCode}: {body}`
  其中 `{statusCode}` 是整数形式的状态码，`{body}` 是响应正文**截断到前 300 个字符**
  （长度 > 300 时取 `body[..300]`，不加省略号）。
- 该异常向上抛给调用方（agent 循环），本模块不重试、不降级、不吞掉。

### 1.3 SSE 解析

按行读取响应流（UTF-8 StreamReader），每行处理规则：

1. 每行开头先检查取消令牌；已取消则立即抛出取消异常（中止本轮流式读取）。
2. 不以 `data: `（含尾随空格，Ordinal 比较）开头的行**直接跳过**（SSE 的 `event:`、`id:`、
   空行、注释行均被忽略）。
3. 载荷 = 该行去掉前 6 个字符后的剩余部分。
4. 载荷等于 `[DONE]` 时结束本轮读取（`break`）。
5. 其余载荷按 JSON 解析。**解析失败（JsonException）必须静默忽略并继续下一行** —— 这是
   服务商的 keep-alive / 心跳帧，不是错误。其他类型的异常不在此吞掉。

单帧内的处理顺序（必须按此顺序，因为用量帧通常 `choices` 为空数组）：

1. **用量**：根对象若含 `usage` 且其为 JSON 对象，读取四个字段并上报计量（见 §3）：
   `prompt_tokens`、`completion_tokens`、`prompt_cache_hit_tokens`、`prompt_cache_miss_tokens`。
   任一字段缺失或非数字时按 **0** 计。
2. `choices` 缺失、非数组、或长度为 0 → 跳过本帧剩余处理。
3. 取 `choices[0]`；无 `delta` → 跳过。
4. **`delta.reasoning_content`**（DeepSeek 推理模式的思考 token）：非空字符串时**只推送到显示
   回调，绝不写入本轮正文，也绝不进入对话历史**。进入思考态的第一帧前必须先推送标记
   `〔思考〕`（无前置换行），随后逐帧推送思考文本。
5. **`delta.content`**：非空字符串时，若当前处于思考态，先推送 `\n〔回答〕` 并退出思考态；
   然后把该片段**同时**追加进本轮正文缓冲并推送到显示回调。
6. **`delta.tool_calls`**（数组）：逐元素按增量拼装：
   - `index` 字段（缺失按 **0**）决定槽位；槽位不足时用空的工具调用占位符补齐到该下标
     （即容忍服务商乱序/跳号下发）。
   - `id` 非空字符串时**覆盖**该槽位的 id。
   - `function.name` 非空字符串时**追加拼接**（`+=`），不是覆盖 —— 函数名可能分片下发。
   - `function.arguments` 非空字符串时**追加拼接**（`+=`），最终得到完整的 JSON 参数文本。
7. 本轮结束时，工具调用列表必须**过滤掉名字为空的槽位**（占位符补齐留下的空洞）后返回。

### 1.4 多轮工具循环

单次对话调用（`ChatStream` 等价物）的语义：

- 入参：可变消息列表（调用方持有）、工具 JSON（可空）、工具执行器（可空）、流式增量回调、
  取消令牌。返回值：最终的助手纯文本回复。
- 最多 **64** 轮（`MaxToolRounds = 64`）。每轮做一次 §1.1–1.3 的完整流式请求。
- 每轮结束后：
  - **无工具调用，或未提供工具执行器** → 把 `{"role":"assistant","content":<本轮正文>}`
    追加进消息列表，返回该正文。循环结束。
  - **有工具调用** → 追加一条助手消息：
    - `role` = `"assistant"`
    - `content` = 本轮正文；**正文为空时必须写 `null`，不能写空字符串**（部分服务商拒绝
      空字符串 + tool_calls 的组合）
    - `tool_calls` = 数组，每项 `{"id":…, "type":"function", "function":{"name":…,"arguments":…}}`，
      `arguments` 为原始 JSON 文本字符串（不重新序列化、不美化）
  - 随后**逐个**执行工具调用：
    - **执行前必须检查取消令牌并在已取消时抛出**。理由（不变量）：F9 重置 / 停止 agent 之后
      绝不允许再执行基于陈旧世界观的工具（会真的开炮）。
    - 参数文本为空串时按 `{}` 解析；解析后把根元素**克隆**再交给执行器（原 JsonDocument 会
      随 `using` 释放，不克隆将得到悬垂引用）。
    - 执行器抛出**任何**异常时，结果替换为 JSON：`{"error":"tool failed: {ex.Message}"}`
      （由序列化器生成，字段名 `error`）。异常绝不向上传播 —— 单个工具失败不得中断整轮对话。
    - 向显示回调推送一行回执，格式**逐字**为：
      `\n🔧 {name}({arguments}) → {result}\n`
    - 把 `{"role":"tool","tool_call_id":<id>,"content":<result>}` 追加进消息列表。
  - 进入下一轮。

### 1.5 工具轮上限与强制收尾

64 轮用尽后**不得**直接返回占位文本，必须做一次强制收尾：

1. 追加一条 `role = "user"` 消息，内容**逐字**为：
   `(系统) 本轮工具调用次数已达上限。停止调用工具, 立即用纯文本总结: 已完成的动作、未完成的意图(下轮优先做什么)。`
   （注意其中的半角逗号与中文标点混用、以及冒号后的空格，逐字保留。）
2. 再做一次流式请求，**工具 JSON 传 `null`**（请求体里没有 `tools` 字段），从协议层面剥夺
   模型继续调用工具的能力。
3. 把该轮正文作为 `{"role":"assistant","content":…}` 追加进消息列表。
4. 返回该正文；正文为空时返回**逐字** `(tool round limit reached)`。

设计意图（必须保留）：历史必须以一条助手回合收尾，不能停在"一堆工具结果 + 占位符"上，否则
下一轮的对话历史不合法且模型会失去决策摘要。

### 1.6 前缀缓存保持（跨轮不变量）

- 消息列表**由调用方持有**，本模块只在其**尾部原地追加**助手/工具/系统提示回合，
  **绝不重写、绝不裁剪、绝不重排、绝不修改已有元素**。整段历史因此逐字节稳定，命中服务商的
  上下文前缀缓存（实测命中率 90%+）。
- 思考内容（`reasoning_content`）永不入历史 —— 一旦入历史，前缀会因服务商不回显思考而抖动。
- 工具回执的 `🔧 …` 展示行只走显示回调，不入历史。
- 历史压缩（auto-compact）由上层 agent 模块负责，本模块不感知；压缩后下一轮必然是缓存 miss，
  属预期成本。

---

## 2. 配置（MelonPreferences）

- 类别名**逐字** `AgentBridge`，持久化于 `UserData\MelonPreferences.cfg`。
- 必须在 mod 初始化早期创建全部条目（含定价条目），之后才可能有任何读取。

| 键（逐字） | 类型 | 默认值 | description（逐字，无则空） |
|---|---|---|---|
| `ApiKey` | string | `""` | `LLM API key (OpenAI-compatible endpoint)` |
| `BaseUrl` | string | `https://api.deepseek.com` | — |
| `Model` | string | `deepseek-v4-flash` | — |
| `MaxTokens` | int | `393216` | — |
| `AutoStart` | bool | `true` | `Start the FDO agent automatically once the scene binds` |
| `LlmControl` | bool | `false` | `Master switch: LLM is allowed to control fire missions (default off; F11 or panel button toggles)` |
| `EnableHttpApi` | bool | `false` | `Expose the local debug HTTP API (fire/draw/requisition endpoints). Keep OFF unless developing — RCE surface for local processes.` |
| `PriceInputCacheMissPer1M` | double | `0.44` | `Input price per 1M tokens (cache miss)` |
| `PriceInputCacheHitPer1M` | double | `0.014` | `Input price per 1M tokens (cache hit)` |
| `PriceOutputPer1M` | double | `1.32` | `Output price per 1M tokens` |
| `PriceCurrency` | string | `USD` | — |

行为要求：

- `BaseUrl` 的读取访问器**必须** `TrimEnd('/')`。其余键原样返回。
- `LlmControl` 是**可写**属性；每次写入后必须**立即** `MelonPreferences.Save()` 落盘（热键/面板
  切换要在游戏进程崩溃前留痕）。
- **启动强制关闭**：初始化的最后一步必须把 `LlmControl` 的值强制置为 `false`。LLM 控制权是
  **每局手动授予**的行为（F11 或面板按钮），绝不从上一局的持久值恢复。
- 定价默认值对应 deepseek-v4-flash **峰时**价；谷时（见 §3）自动半价，配置里不需要第二套价。
- `MaxTokens` 默认 393216 = DeepSeek 384k 最大输出的顶格值（上下文 1M）。

**运行期陷阱（不变量）**：游戏运行中**绝不允许**手改 `MelonPreferences.cfg` —— 任何一次
`Save()` 都会按内存值整文件重写，手改必被清。运行中改开关只能走热键/面板路径。

---

## 3. 用量计量

会话累计（进程生命周期内累计，跨局不清零）。

### 3.1 状态量

- `PromptTokens`、`CompletionTokens`、`CacheHitTokens`、`CacheMissTokens`（各 64 位累加）
- `Rounds`（计量帧计数）
- `LastPromptTokens`：**最近一轮**的 prompt token 数 —— 即当前上下文窗口占用量，供 UI 显示与
  上层 auto-compact 阈值判断（阈值 400_000，判断逻辑在 agent 模块）
- 累计费用 `EstimatedCost`（按轮累加，每轮已带峰谷因子）

所有读写必须在同一把锁内进行（写入方是 agent 后台线程，读取方是 Unity 主线程的 UI 绘制）。

### 3.2 峰谷判定

`IsOffPeak` = 把当前 UTC 时间加 **8 小时**（北京时间 UTC+8）取当日时刻，落在
**[00:30:00, 08:30:00)** 半开区间内即为谷时。谷时全价目表 **×0.5**。

### 3.3 单轮计费公式

```
factor = IsOffPeak ? 0.5 : 1.0
input  = (cacheHit + cacheMiss > 0)
         ? cacheHit/1e6 * PriceInputCacheHit + cacheMiss/1e6 * PriceInputCacheMiss
         : prompt/1e6  * PriceInputCacheMiss
roundCost = factor * (input + completion/1e6 * PriceOutput)
```

即：服务商上报了缓存明细就按命中/未命中分别计价；一个都没报（非 DeepSeek 端点）则**整段
prompt 按未命中价保守估算**。价格单位一律"每 1M tokens"。

### 3.4 计量副作用

每次计量必须写一条事务日志，`type` = `usage`：

- `text` 格式**逐字**：`round: in={prompt} (hit {cacheHit}/miss {cacheMiss}) out={completion} {peak}`
  其中末位在谷时为 `off-peak`、峰时为 `peak`。
- `data` 对象字段名逐字：`prompt`、`completion`、`cacheHit`、`cacheMiss`、`roundCost`、`totalCost`
  （`totalCost` = 累加后的总费用）。

### 3.5 摘要串

供面板显示的摘要格式**逐字**（`N0` = 千分位分组整数，`F3` = 三位小数）：

```
tokens: in {PromptTokens:N0} (cache hit {CacheHitTokens:N0}) out {CompletionTokens:N0} · {Rounds} rounds · ≈{EstimatedCost:F3} {PriceCurrency}
```

谷时在末尾追加**逐字** ` (谷时半价)`（前导一个空格）。分隔符是中点 `·`，两侧各一个空格。

---

## 4. 事务日志（JSONL）

- 目录：`{MelonEnvironment.UserDataDirectory}\IronNestAgentBridge`，**首次写入时惰性创建**。
- 文件名：`transactions-{yyyyMMdd}.jsonl`，日期取**本地时间**，且**每次写入都重算文件名** ——
  跨零点自动滚动到新文件，无需重启。
- 每行一条 JSON，字段名与顺序**逐字**：
  - `ts` = 本地时间，格式串**逐字** `yyyy-MM-dd HH:mm:ss.fff`
  - `type` = 事件类型串（见下）
  - `text` = 人类可读描述
  - `data` = 任意可序列化对象，可为 `null`
- **逐行追加并落盘**（append + 每行 flush），保证崩溃不丢已写行。
- **线程安全**：写入必须整体持锁（agent 后台线程与 Unity 主线程都会写）。
- **绝不抛异常**：整个写入过程包在 try/catch 中，任何异常（磁盘满、权限、路径）都必须
  静默吞掉。不变量：**日志系统永远不能拖垮 agent**。

已在用的 `type` 取值（重实现须保持一致，其余模块按此过滤/检索）：
`usage`、`compact`、`tool`、`decision`、`fire`、`cancel`、`adjust`、`turret`、`agent`、
`mission`、`reset`、`requisition`、`scout_plane`。

**编码注意**：默认 JSON 序列化器会把非 ASCII 转义成 `\uXXXX`，文件因此是纯 ASCII 的 UTF-8；
若重实现改用宽松编码器输出中文明文，必须确保写文件使用 UTF-8（**不得**依赖中文 Windows 的
ANSI/GBK 默认编码），否则日志乱码。

---

## 5. 跨模块契约

### 5.1 本模块对外暴露

- **`ChatStream(messages, toolsJson, toolExecutor, onDelta, ct) -> string`**
  - `messages`：`List<object>`，**调用方所有**。调用方负责放入 system 提示与新的 user 消息；
    本模块只追加 assistant/tool/（收尾时的）user 回合。调用方必须保证同一 agent 会话中始终
    传同一个列表实例（前缀缓存）。
  - `toolsJson`：OpenAI `tools` 数组的 JSON 文本；`null` = 本次禁用工具。
  - `toolExecutor`：`Func<string toolName, JsonElement args, string resultJson>`。**在 agent 后台
    线程上被同步调用**；需要访问游戏对象的实现必须自行经主线程调度并等待。返回值直接成为
    `tool` 消息内容（约定为 JSON 文本，但本模块不校验）。
  - `onDelta`：`Action<string>`，接收正文片段、思考片段、思考/回答分隔标记、以及 `🔧` 工具回执行。
    **在流式线程上同步调用**，实现必须非阻塞（当前实现只做字符串缓冲 + 赋值给 UI 可读字段）。
  - `ct`：取消令牌；停止 agent / F9 重置时触发。取消异常从本方法向上抛出。
  - 返回：最终助手纯文本（agent 用作"决策理由"，截断到 500 字符后显示）。
- **`AgentConfig`**：`ApiKey / BaseUrl / Model / MaxTokens / AutoStart / EnableHttpApi /
  PriceInputCacheMiss / PriceInputCacheHit / PriceOutput / PriceCurrency` 只读；`LlmControl` 读写。
  `Initialize()` 必须在 mod 启动时、启用 HTTP API 与启动 agent **之前**调用。
- **`UsageMeter`**：`Summary`、`LastPromptTokens`、`EstimatedCost` 及各计数供面板与 agent 读取；
  `AddRound` 仅由流式解析器调用。
- **`TransactionLog.Write(type, text, data?)`**：全 mod 共用的落盘通道。

### 5.2 本模块依赖的外部行为

- agent 模块负责：system 提示词、auto-compact（阈值 `400_000` prompt tokens，读
  `UsageMeter.LastPromptTokens`）、工具分发与结果字符串（含 `[@HH:mm]` 世界时钟前缀与
  `[随查战场新事件]` 搭车段）、取消令牌的生命周期。
- 面板 UI 读取 `AgentConfig.Model`、`UsageMeter.Summary`、`UsageMeter.LastPromptTokens`
  （每帧读，因此计量的锁必须便宜且不可重入死锁）。
- 主 mod 读取 `AgentConfig.EnableHttpApi` 决定是否开本地调试 HTTP 服务；读写
  `AgentConfig.LlmControl` 响应 F11 / 面板按钮与任务结束自动停机。
- MelonLoader 提供 `MelonPreferences` 与 `MelonEnvironment.UserDataDirectory`。

---

## 6. 不变量与防御性规则

1. **绝不在 Unity 主线程上调用 `ChatStream`**。它内部对 HTTP 与流读取做同步阻塞等待
   （sync-over-async），主线程调用会挂死游戏。它只在 agent 的后台线程运行。
2. **工具执行前必检取消**。停止/重置后残留的工具调用会用陈旧世界观真的开炮。
3. **工具异常必被吞成 JSON 错误结果**，绝不冒泡中断整轮对话。
4. **日志写入永不抛异常**。
5. **消息历史只追加不改写**（前缀缓存）；`reasoning_content` 与 `🔧` 回执行永不入历史。
6. **`content` 为空且带 `tool_calls` 时必须写 `null`**，不能写 `""`。
7. **JSON 解析失败的 SSE 帧必须容忍**（keep-alive），其它异常不得一并吞掉。
8. **用量帧必须在 `choices` 空数组检查之前处理** —— 服务商的用量帧通常没有 choices。
9. **计量与费用的全部状态读写共用一把锁**；`Summary` 与 `EstimatedCost` 也在锁内取值。
10. **配置初始化必须先于一切读取**；启动时强制 `LlmControl = false`。
11. **编码陷阱**：请求正文、SSE 读取、日志写入三处都必须显式 UTF-8。中文 Windows 的默认
    ANSI 是 GBK，任何一处漏掉显式 UTF-8 都会产生乱码（本仓库有过用 PowerShell 改中文源文件
    导致全文乱码的事故）。
12. **`🔧`、`〔思考〕`、`〔回答〕`、`·`、`≈`、`谷时半价` 等非 ASCII 字面量必须逐字保留** ——
    面板与日志的既有格式依赖它们。
13. **Il2Cpp 边界**：本模块本身是纯托管代码（`HttpClient` / `System.Text.Json` / `MelonPreferences`），
    不直接触碰 Il2Cpp 对象；所有游戏侧访问由工具执行器（agent/tools 模块）负责，并由其自行
    包 try/catch 与主线程调度。重实现时不要把游戏访问下沉进本模块。

---

## 7. 逐字保留数据块

本模块的四个文件中**没有**大段自然语言数据（SystemPrompt / MapIntelTable / 学说文本均在
`agent/FdoAgent.cs`，属其他模块）。以下短字面量已在上文正文中逐字给出，重实现时按上文抄，
或回原处核对：

- `C:\Users\stevenli\Codes\IronNestAgentBridge\agent\LlmClient.cs:78-82` — 工具轮上限的强制收尾
  user 消息（§1.5）。
- `C:\Users\stevenli\Codes\IronNestAgentBridge\agent\AgentConfig.cs:19-26, 57-60` — 各配置键的
  description 文本（§2 表格）。
