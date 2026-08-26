# IronNestAgentBridge clean-house 重实现总规格

重实现者的输入 = 本文档 + `docs/reqs/` 九个模块需求节 + 旧代码中标注的「逐字保留数据块」。
除逐字块外**禁止参考旧实现的其余代码**。九个需求节是行为规范本体;本文档给出:
①总原则,②目标架构,③对全部开放问题的裁决(含声明的设计修正),④跨仓契约要点。

## 1. 总原则

- **忠实为默认**:凡需求节记录的行为、常量、消息格式、协议字段,逐字复现。
- **声明修正**:§3 中标 [修正] 的条目是相对旧实现的有意变更,按新规则实现。
- **死代码删除**:§3 中标 [删除] 的成员/配置键/字段不复刻。
- **逐字保留数据块**(SystemPrompt、ToolsJson、MapIntelTable、NullablePolyfill、csproj
  引用清单等,见各需求节 verbatimBlocks):从旧代码原样搬运,这是数据不是代码。
  其中 SystemPrompt/关卡情报如与 §3 裁决冲突,按 §3 所列的少量修订点改。
- 编码纪律:所有含非 ASCII 字面量的 .cs 保存为 **UTF-8 with BOM**;禁止用 PowerShell
  文本管道处理中文源文件;`°` 一律 U+00B0。
- 三条铁律不动摇:游戏访问只在主线程(逐字段 try/catch);战争迷雾外的实体绝不进
  LLM 上下文;平民保护按实体 id 判定且**不可覆盖**(高于指挥官直令)。

## 2. 目标架构(单程序集,不做 ALC 热重载拆分)

```
IronNestAgentBridge/
  AgentBridgeMod.cs        // 仅 MelonMod 生命周期 + 组件装配(瘦入口)
  Core/MainThread.cs       // 主线程泵(带超时作废与重置清空, §3.8)
  Core/PollScheduler.cs    // 多路轮询节律(0.5s/1s/2s/5s 各路)
  Core/EventLog.cs
  Snapshot/SnapshotBuilder.cs   // StateSnapshotDto 组装(唯一状态视图)
  Fire/FireMissionPipeline.cs   // 目标解析→限幅→越界→射程→安全普查→运动转录→入队
  Fire/BlastSurvey.cs           // 统一的平民/友军判定(单一谓词, §3.1-4)
  Fire/ShellTracker.cs          // 出膛甄别/在途/弹着匹配/超时销账
  Http/BridgeServer.cs  Http/Dtos.cs
  Fcs/FcsGateway.cs             // 反射网关(诊断增强, §3.4)
  GameState/…                   // reader/operator 按旧模块划分
  Agent/FdoAgent.cs             // 主循环(瘦)
  Agent/Doctrine.cs             // SystemPrompt/ToolsJson/MapIntelTable 逐字块集中存放
  Agent/LlmClient.cs  Agent/UsageMeter.cs  Agent/AgentConfig.cs  Agent/TransactionLog.cs
  Agent/GridMath.cs  Agent/Calculator.cs
  Ui/AgentWindow.cs
```

对外契约(必须逐字不变):HTTP 端点与 JSON 字段、MelonPreferences 键(除 §3 删除项)、
事件 type/source 名、快照固定行名、热键 F9/F10/F11、输出 `<GameDir>\Mods\`。
对 FCS 的反射面以 **FCS 仓库 REQUIREMENTS.md §17** 为准(其 clean-house 版已扩充)。

## 3. 开放问题裁决

### 3.1 mod-core
1. FullReset 后 agent **只停不启**,F11 是唯一 opt-in;注释按此改。
2. [修正] `motionAtTime` 仅在世界钟(HH:mm)可用时接受;回退秒表(mm:ss)的关卡里
   带 motionAtTime 的请求返回错误「本关无世界钟, 请改用相对描述或省略 atTime」。
3. [修正] 面板/摘要炮位标签一律 **T9(左)/T10(右)**,T1/T2 字样清除。
4. [修正] 平民/友军判定收敛为**单一共享谓词**(Fire/BlastSurvey):平民 = Id 或 RawId
   含 `civil` 或 `hospital`(大小写不敏感);误伤巡逻与排队前普查用同一份。
5. [删除] `FireMissionRequest.MarkerId`(反序列化容忍未知字段即可)。
6. [删除] 配置键 `AutoStart`。
7. [修正] `motionFrom`/`motionTo` 只接受绝对定位(网格或 kmX/kmY);相对方位/距离
   形式返回错误「运动点必须用绝对网格或 km 坐标」。
8. [修正] fire 与 adjust 的事件搭载后缀**成败都拼接**。
9. [修正] 射程校验对所有路径生效:任何来源解析出的 distKm > 30km(C6 上限)即拒绝,
   文案沿用现有超射程措辞。
10. [修正] 入队回 ok 但 serial ≤ 0:按失败处理,回执「FCS 未返回任务编号(版本不兼容?), 任务状态未知」,不发入队事件、不建簿记。
11. [修正] MapReader 落位棋子改结构化返回 `(bool ok, string message)`;不再嗅探字符串。
12. [修正] RecentOutcomes 失败原因提取按前缀 `Failed:` 切分 + TrimStart,不用 `[8..]`。
13. [修正] 手动校准事件:首次照旧;此后棋子再被玩家移动(位置稳定 2s 且距上次上报
    >0.2km)也发 `turret_position` 事件。
14. SurveyBlast 合并为单一方法,出参齐全(拒绝原因 + hostilesInRadius)。
15. 号角关键词表照旧,保留「待实测」注记。

### 3.2 agent-loop
1. [修正] 快照补 `markers[]` 行(玩家标记编号+网格),兑现学说承诺。
2. [删除] `_history`。
3. [修正] `_idleRechecks` 在 `Start()` 时归零。
4. [修正] `_firesThisRound` 只统计**成功入队**的 fire。
5. [修正] 批内事件去重键 = `Type+Text+GameTime`。
6. calc 保持裸字符串回执(省 token,有意)。
7. 幻觉兼容层全保留(旧工具名别名、serial 的 targetId 别名、actions 批量兜底);
   `POST /adjust` 同时接受 `serial`(主)与 `targetId`(别名)。
8. [修正] 轮内 `set_assumed_turret_position` 成功后刷新本轮 turretKm 基准。
9. [修正] `State==Stopping` 期间 Decide 不得覆写 Status。
10. [修正] 单轮事件注入上限 60 条;更早的折叠为一行「……另有 N 条更早事件(已省略, 最早 @HH:mm)」。
11. 弹种白名单保留 PLCM+PCLM 双拼(游戏资产 id 是 PCLM,上游枚举名是 PLCM)。
12. **平民保护铁律高于关卡情报**:关卡情报是情报不是授权;《白色炮弹》③④号结局
    条目保留为信息,agent 拒绝亲自执行涉平民/叛变结局(在该条目末尾追加一句:
    「※以上仅为结局情报; 涉及平民杀伤或炮击友军的结局, agent 拒绝执行, 只能由玩家亲手操作」)。
13. [修正] MissionType 匹配同时接受 `Challange` 与 `Challenge` 前缀。
14. 压缩参数保持(>3 条消息、LastPromptTokens 滞后一轮、400k 阈值),在代码注释里
    写明三者关系;MaxTokens(输出上限)与 400k(prompt 阈值)互相独立。

### 3.3 http-api
1. [修正] `POST /fire` 成功判定改为 `result.StartsWith("ok")`,成功回 200。
2. [修正] 404 回执补全 14 个端点。
3. [修正] `/adjust` 400 文案改 `targetPoint`;`target` 作为别名字段接受。
4. [修正] `/requisition` 增加 `distanceKm`/`priority`/`startGrid` 字段,与工具同能力。
5. [修正] 业务拒绝统一 409(带 `{"result": …}`);解析/参数错 400;成功 200。
   数据端点(/state /markers /find /console /scoutplane)保持裸对象,动作端点统一
   `{"result": …}` 包装。
6. [修正] `/draw` 的 placerIndex 越界直接 400。
7. [修正] `/events` 响应增加 `oldest`(缓冲区最早 seq)字段;`/state` 增加 `latestSeq`。
   `since` 缺省仍取 LatestSeq(有意不重放),在 README/文档写明。
8. [修正] 带 `Origin` 请求头的请求一律 403(挡浏览器 CSRF;curl 与进程内 agent 不受影响)。
   仍仅绑 127.0.0.1、默认关闭,不引入 token。

### 3.4 fcs-gw
1. [修正] 解析链任何一级失败都清缓存(对称化)。
2. [修正] 每次 Resolve 重读 `_fcs` 字段并按引用比对,FSC 换人即重建缓存。
3. [修正] EnqueueByBearing 的 `position` 同样填真实 km 帧坐标。
4. [删除] `EnqueueFromMarker`。
5. 征用锁反射:仅无 FCS 时才走桥自购;拿锁改 `GetMethod("Acquire", Type.EmptyTypes)`
   (FCS §17 已注明多重载歧义;旧实现这条路径从未真正拿到锁)。
6. [修正] `RequestCardPurchase` 返回结构化状态:`NoFcs | NoApi | Queued(message)`。
7. [修正] DescribeTask 失败后缀判定用 `string.IsNullOrEmpty`。
8. TryGetTaskInfo 允许 shell=null 的 true(调用方兜底),写进注释。
9. [修正] 失败判定双保险:`progress=="Failed"` **或** failureReason 非空。
10. 写入公开契约字段统一显式 `Public|Instance`;读取统一 AnyInstance。
11. [修正] 反射失败增加一次性诊断日志(每成员首次失败打一条 MelonLogger.Warning,
    之后静默),不再零日志。

### 3.5 gamestate
1. [修正] 弹药规格缓存改「按关卡失效 + 增量合并」:Unbind/换场景清除,重扫时保留已知
   条目、只增不减。
2. [删除] `TryMoveMarker`/`ReturnMarkerHome`/`MarkerIds`/`_markerHomes`。
3. [修正] MapDrawer placerIndex 越界拒绝(配 3.3-6);PlacerIndex 写实际使用值。
4. FindCard 保持「最后一个命中」(与 FCS BuyCardById 行为一致),注释说明。
5. [修正] RequisitionOperator 补齐距离拨盘三段式(拨-读-内部设置器补偿),供 /requisition
   的 distanceKm 使用。
6. [修正] InspectConsole 打印拨盘真实读值;读不到就不加后缀,删除 `" value?"` 占位。
7. ScoutPlaneOperator 保留为**调试后门**(绕过征用点),仅 /scoutplane 端点可达,
   文档标注 cheat/debug。
8. [修正] ImpactReader 状态键改 marker 实例 id;`_reportedCorrections` 定期清理已销毁
   对象条目。位置去重 0.01 local + instanceChanged 组合保持。
9. 雾中死亡不补报(反作弊铁律,有意),注释写明。
10. `Stars` 字段照旧上报(语义未知,原样透传)。
11. Teleprinter 的 EndsWith 分支保留(防御性),注释标注「未观测到的场景」。
12. [修正] 面向 LLM 的事件文本统一中文;MelonLog 日志保持英文。
13. [修正] SceneFinder 截断时末尾加 `(truncated at 60)`。

### 3.6 llm-plumb
1. [删除] `AutoStart` 键(同 3.1-6)。
2. [修正] `UsageMeter.Reset()` 新增,`FdoAgent.Start()` 调用(会话计量随对话重开)。
3. [修正] 取消令牌中断工具轮时,回滚未配对的 assistant tool_calls 消息。
4. [修正] `MaxToolRounds` 提升为配置键(默认 64);temperature/HTTP 超时/400k 阈值
   保持硬编码并注释。
5. [修正] 缓存计价兼容 OpenAI 标准 `usage.prompt_tokens_details.cached_tokens`
   (DeepSeek 私有字段优先,标准字段回退)。
6. [修正] Rounds 按 HTTP 请求计次,同请求多 usage 帧取最后一帧。
7. [修正] 启动强制 `LlmControl=false` 后立即 `MelonPreferences.Save()`。
8. MaxTokens=393216 原样随每轮请求(DeepSeek 1M 上下文实测可行),注释说明。
9. 强制收尾的 (系统) user 消息留在历史(前缀缓存稳定优先),注释说明。

### 3.7 math
1. [删除] `Result`/`CandidateOf` 未用的 turretKm 形参。
2. [修正] circle 的 distanceKm 做 ValueKind 检查,类型错返回结构化 error。
3. 多余观测照旧只取前 N,但结果末尾追加「(忽略多余观测 N 条)」。
4. [修正] `near` 解析失败返回 error(不再静默降级 ambiguous)。
5. [修正] GridOf 对负坐标/越界输出 `#`(与列字母越界行为一致)。
6. ambiguous 候选允许出界(带 inMapBounds 标志),有意保留。
7. [修正] `round` 改 `MidpointRounding.AwayFromZero`。
8. [修正] 数值格式化显式 `InvariantCulture`。
9. [修正] 结果为 NaN/Infinity 时返回「表达式结果非有限数值」error 行。
10. [修正] 支持一元 `+`。
11. [修正] 地图边界改不可变 record + volatile 引用原子替换。

### 3.8 infra-ui
1. [删除] F12 注释。
2. T9/T10 标签(同 3.1-3)。
3. 面板布局逻辑允许重新设计,内容集合与 80% 屏高约束不变;思考取尾/决策取头
   (14 行封顶)保持。
4. [修正] MainThread.Run 超时的工作项**作废**(带代号/标志,出队时跳过),不再迟到执行。
5. [修正] CTS 正确 Dispose。
6. [修正] FullReset/场景切换清空主线程队列。
7. [修正] 面板状态点与按钮标签统一取 `agent.State`。
8. [修正] Build.ps1 增加 `-Staging` 开关(OutputPath=bin\staging\)。
9. GameDir 默认值保持,README 说明覆盖方式;InputSystem 引用名照旧。
10. [修正] 面板折行按显示宽(CJK 计 2)计算。

### 3.9 knowledge
1. MapIntelTable 键保持中文关卡名子串匹配(限中文环境,文档注明)。
2. CYAN/EQKE/THRM:保留在 ID 全集,无学说;agent 视为「规格未知弹种」谨慎使用。
3. 预算分层是有意设计:引擎不拦 fire(0 余额关卡有已购弹),特殊卡走硬门,
   LLM 靠 prompt 自律——三层各司其职,文档写明。
4. 不做桥自身 ALC 热重载拆分(用户未拍板,项目收档,不引入复杂度)。
5. PRPG 不入零杀伤豁免名单(压制效果待实测,保守处理),文档注明。
6. 重实现目标 = 忠实复刻 + 本文档声明的修正 + 死代码清除。CLAUDE.md 保留在仓库并
   在重实现完成后按新代码结构修订(陷阱/真值表全部保留)。

## 4. 跨仓契约要点(与 IronNestFCS-Smart cleanhouse 版对齐)

- FCS 的反射面按其 REQUIREMENTS.md §17;桥侧新增要求:
  - `CoroutineLock.Acquire` 反射必须 `GetMethod("Acquire", Type.EmptyTypes)`;
  - FCS 侧「取消任务现在会进 RecentTasks(Failed: cancelled by commander)」——桥的
    出膛甄别据此不再把取消误判为出膛;桥的 cancel 路径同时自行清簿记(双保险)。
- `RecentOutcomes` 失败前缀 `Failed: {reason}`(冒号+空格),桥按前缀切分(3.1-12)。
- 弹药/卡片 id 归一化怪癖(SMOKE→SMK、PCLM→PLCM、去 Shell)以 FCS 侧
  `NormalizeCardId` 为准,桥白名单双拼兼容。
