# IronNest Agent Bridge

独立的 MelonLoader mod,把 *Iron Nest: Heavy Turret Simulator* 的战场信息与
[IronNestFCS Smart](https://github.com/HisenWeb/IronNestFCS-Smart) 火控系统暴露为本地 HTTP API,
并内置一个 LLM agent(默认 DeepSeek)担任"射击指挥官(FDC)":读电报 → 侦察 → 解算 → 排火力任务 →
校射修正,全程自主。完整战役已由 agent 实战通关(含四结局终局关)。

FCS 负责"自动化操作",本 mod 在其上补齐它刻意不做的"战术层"接入点。
**与 FCS 完全解耦**:仅通过反射对接,FCS 不存在时读取功能照常工作。

## 能力一览

- **战场感知**:两台打字机(统帅部电文/战场报告)、指挥桌实体(严格尊重战争迷雾)、
  弹着标记与黄箭头修正提示(只转述玩家可见的模糊度)、反炮击倒计时、征用点余额、
  24h 世界时钟时间轴(所有事件/快照/工具回执统一 [@HH:mm])。
- **火力指挥**:唯一任务编号 #N;纯坐标入队(不占用玩家地图标记);运动模型移动靶
  (实体跟踪或电报转录,FCS 侧装药感知提前量);最后时刻改瞄(adjust_fire,FCS 永不等待);
  任务时效(validForSeconds,过期自动撤销);发射顺序尊重优先级(跨批次)。
- **安全层**:友军误伤排队拦截 + 5s 弹着区闯入监视;零杀伤弹(SMK/STAR/TEAR/DRIL)豁免;
  **平民保护不可覆盖**(按实体 id 判定,无视阵营标注——某终局关会把难民标成敌方);
  检查火力条令(误伤停火→整改→恢复,不永久趴窝)。
- **指挥通道**:`POST /command` 指挥官口头直令,权威高于游戏内统帅部;
  关卡情报库按当前任务名动态注入(每图打法只在该图生效)。
- **Agent 工程**:持久多轮对话保前缀缓存;工具结果尾部搭载执行期间新到的战场事件(同轮反应);
  400k tokens 自动压缩接班;空转指数退避;工具集含 solve_target 三角解算、calc 角度制计算器、
  distance_between/entities_near 等——LLM 严禁心算。

## HTTP API(127.0.0.1:17171,仅本机,默认关闭需在配置开启)

- `GET /state` — 全量快照(地图实体/打字机/火炮/FCS 队列/在途炮弹/余额/关卡与模式)
- `GET /events?since=N&timeoutMs=25000` — 长轮询事件流
- `POST /fire` — 排火力任务:`{"entityId"|"target"|"bearingDeg"+"distanceKm", "shell", "priority", "validForSeconds", ...}`
- `POST /adjust` — 改瞄已排任务(按 #N)
- `POST /command` — 指挥官直令(中文体请以 UTF-8 文件 `--data-binary` 发送)
- `POST /print` / `/horn` / `/turret` / `/requisition` / `/draw` 等,详见 `Http/BridgeServer.cs`

弹种:AP APHE ATMC CLMN CYAN DRIL EQKE FLCH HCHE HE INCN LE PCLM PHGN PRPG SMK STAR TEAR THRM WP

## 构建 / 安装

```
dotnet build -c Release
```

`GameDir` 属性指向游戏根目录(csproj 里有默认值,可用 `-p:GameDir=...` 覆盖)。
产物直接输出到 `<GameDir>\Mods\IronNestAgentBridge.dll`。
依赖:MelonLoader ≥ 0.7(IL2CPP),可选 [IronNestFCS Smart](https://github.com/HisenWeb/IronNestFCS-Smart)(火力任务下发需要它)。

## 内置 Agent

游戏内 **F10** 呼出控制面板(启停、决策理由、行动日志),**F11** 总控 LLM。
配置在 `UserData\MelonPreferences.cfg`:

```toml
[AgentBridge]
ApiKey = "sk-..."
BaseUrl = "https://api.deepseek.com"
Model = "deepseek-v4-flash"
MaxTokens = 393216
AutoStart = true
EnableHttpApi = true
```

## 已知边界

- 地图标记:T9/T10 由 FCS 用作左右炮瞄点指示,T1–T8 完全归玩家;桥不移动任何标记。
- FCS 调度有焦点门槛,游戏后台时任务挂起;agent 同步暂停(不烧 token)。
- 所有游戏状态访问经主线程泵;HTTP 线程绝不直接碰 Il2Cpp。
- F9/场景切换自动重绑定。

项目开发全程知识库见 `CLAUDE.md`(逆向工程结论、陷阱、学说设计)。
