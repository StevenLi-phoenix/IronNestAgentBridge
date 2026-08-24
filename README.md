# IronNest Agent Bridge

独立的 MelonLoader mod，把 *Iron Nest: Heavy Turret Simulator* 的战场信息与
[IronNestFCS Smart](https://github.com/HisenWeb/IronNestFCS-Smart) 火控系统暴露为本地 HTTP API，
供外部 LLM agent 担任"射击指挥官"：读电报 → 看地图 → 排火力任务。

FCS 负责"自动化操作"，本 mod 在其上补齐它刻意不做的"战术层"接入点。
**与 FCS 完全解耦**：仅通过反射对接，FCS 不存在时读取功能照常工作。

## 信息源

| 游戏概念 | 实现 | 读取方式 |
|---|---|---|
| 最高统帅部电文 | `Teleprinter` (Primary) | `CaptureMissionState().CurrentFullRich`，轮询 diff |
| 战场报告（相邻打字机） | `Teleprinter` (Secondary) | 同上 |
| 指挥桌地图目标（不上电报的新目标） | `Fire Mission Root` 下的 `EntityLocation`/`MapEntity` | 0.5s 快照 diff → 揭示/移动/受损/摧毁事件 |
| 玩家标记 T1–T4 | `Draggable Surface` 下 `MapToken_Artillery` | TMP 文本识别编号 |
| 火炮物理状态 | `Il2Cpp.GunController`（GunLeft/GunRight） | 直读 |
| FCS 任务队列 | `FSC`（反射，跨 F9 热重载自动重解析） | 直读公开属性 |

## HTTP API（127.0.0.1:17171，仅本机）

- `GET /state` — 全量快照：地图实体（含方位角/距离解算）、标记、两台打字机全文、火炮、FCS 状态
- `GET /events?since=N&timeoutMs=25000` — 长轮询事件流：
  `telegraph_message` / `entity_revealed` / `entity_moved` / `entity_damaged` / `entity_destroyed` / `fcs_task_update`
- `POST /fire` — 排火力任务，两种方式：
  - `{"entityId": "...", "shell": "HE", "markerId": 4}` — 桥接把 4 号标记移到目标上，
    再调 FCS 自己的 `MapTable.GetMarkTarget`（与人工点击完全同一条数学路径）
  - `{"bearingDeg": 273.5, "distanceKm": 9.7, "shell": "AP"}` — 直接给射击诸元
- `POST /print` — `{"which": "secondary", "lines": ["..."]}` 在打字机上打印（LLM 回执电文）

弹种：AP APHE ATMC CLMN CYAN DRIL EQKE FLCH HCHE HE INCN LE PLCM PHGN PRPG SMK STAR TEAR THRM WP

## 构建 / 安装

```
dotnet build -c Release
```

`GameDir` 属性指向游戏根目录（csproj 里有默认值，可用 `-p:GameDir=...` 覆盖）。
产物直接输出到 `<GameDir>\Mods\IronNestAgentBridge.dll`。
依赖：MelonLoader ≥ 0.7（IL2CPP），可选 IronNestFCS Smart（火力任务下发需要它）。

## 已知边界

- FCS 的调度有焦点门槛（`Application.isFocused` + 0.25s），游戏后台时任务会挂起在 Pending。
- 所有游戏状态访问都经 `MainThread.Pump()` 走主线程；HTTP 线程绝不直接碰 Il2Cpp。
- F9 / 场景切换后桥接自动重绑定，agent 无感。
- `BearingDeg/DistanceKm` 的实体解算是估算（与 FCS 同一比例常数 3.8164）；
  下发火力时优先用 `entityId` 路径，走 FCS 原生解算。

## Agent 接入

`agent/agent.py` 是最小参考实现：长轮询事件 → 组装战场上下文 → 调 LLM 决策 →
执行 `/fire`。替换其中的 `call_llm()` 即可接任意第三方模型。
