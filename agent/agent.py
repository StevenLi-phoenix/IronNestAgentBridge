"""Minimal LLM fire-direction-officer loop for IronNest Agent Bridge.

Long-polls bridge events (telegrams, map reveals), asks an LLM what to do,
executes fire missions. Swap call_llm() for any provider (Anthropic shown).

    pip install anthropic requests
    python agent.py
"""

import json
import time

import requests

BRIDGE = "http://127.0.0.1:17171"

SYSTEM_PROMPT = """\
你是重型要塞炮"铁巢"的射击指挥官(FDC)。你会收到:
- 最高统帅部电文(primary): 任务指令、弹药限制、反炮兵警告
- 战场报告(secondary): 观测员的方位角交汇报告
- 指挥桌事件(map): 新揭示/移动/受损/摧毁的目标
- state快照: 所有可见目标的方位角/距离/护甲/免疫弹种、火炮与FCS状态

你的职责是战术决策: 打谁、用什么弹、什么顺序。执行完全由FCS自动完成:
你排任务后FCS会自动购弹、装填、装药、调仰角、转炮塔。**任何时候都可以排任务**,
不要因为guns显示isReloading/canFire=false而等待——那是炮的常驻机械状态,
FCS会处理好一切。fcs.pendingCount/leftTask/rightTask才反映任务执行进度。
规则:
- 遵守统帅部电文中的弹药限制与优先目标指令
- 注意目标护甲与免疫弹种(ImmuneShells), 地下工事需AP系
- 反炮兵威胁下优先高价值目标
- 战争迷雾: entities[]是当前唯一的已揭示目标清单, 为空就说明没有任何目标被揭示。
  entityId必须一字不差地取自entities[]里实际存在的id, 严禁凭空猜测或编造id。
  未揭示目标只能根据电报情报三角定位后用bearingDeg+distanceKm盲射
  (方位角以炮塔为原点, 正北=0°顺时针; 距离单位km)。
- 坐标换算: 电文中的网格如"H5 0:9"表示 kmX=字母序号+第一个子格/10 (A=0,B=1,...,H=7,
  即kmX≈7.0), kmY=(行号-1)+第二个子格/10 (即kmY≈4.9)。快照中的mapX/mapY换算:
  kmX=10.016+mapX*3.8164, kmY=5.235+mapY*3.8164。两点间: dx=kmX2-kmX1, dy=kmY2-kmY1,
  距离=sqrt(dx²+dy²) km, 从点1看点2的方位角=atan2(dx,dy)转成0~360°。
  炮塔自身位置见快照turretMapX/turretMapY(注意先换算成km坐标再参与计算)。
  战场报告给出的"自X的方位角"是从X点出发的观测线, 两条线相交即目标位置;
  "自X距离Y"则是以X为圆心的圆。逐步写出你的计算过程再给出结论。
- 每次决策输出JSON, 两种action格式:
  {"actions": [{"entityId": "<必须是entities[]中存在的id>", "shell": "HE"},
               {"bearingDeg": 75.0, "distanceKm": 9.1, "shell": "AP"}], "reason": "..."}
  不开火时输出 {"actions": [], "reason": "..."}
- 不要重复排已经下达过的任务(见"你此前的决策"), FCS队列里的任务会自动执行完。
  同一目标一般一发命中即毁; 只有观测到未命中/目标幸存时才补射。
"""


import os

LLM_BASE = os.environ.get("LLM_BASE_URL", "https://api.deepseek.com")
LLM_MODEL = os.environ.get("LLM_MODEL", "deepseek-v4-flash")
LLM_KEY = os.environ["LLM_API_KEY"]


def call_llm(context: str) -> dict:
    """OpenAI-compatible chat completions (DeepSeek by default)."""
    r = requests.post(
        f"{LLM_BASE}/chat/completions",
        headers={"Authorization": f"Bearer {LLM_KEY}"},
        json={
            "model": LLM_MODEL,
            "messages": [
                {"role": "system", "content": SYSTEM_PROMPT},
                {"role": "user", "content": context},
            ],
            "max_tokens": 4096,
            "temperature": 0.3,
        },
        timeout=120,
    )
    r.raise_for_status()
    text = r.json()["choices"][0]["message"]["content"]
    start, end = text.find("{"), text.rfind("}")
    return json.loads(text[start : end + 1]) if start >= 0 else {"actions": []}


def main() -> None:
    since = 0
    history: list[str] = []
    print(f"[agent] watching {BRIDGE} ...")
    while True:
        try:
            r = requests.get(
                f"{BRIDGE}/events", params={"since": since, "timeoutMs": 25000}, timeout=30
            )
            payload = r.json()
        except requests.RequestException as e:
            print(f"[agent] bridge unreachable ({e}); retrying in 5s")
            time.sleep(5)
            continue

        events = payload.get("events", [])
        since = payload.get("latest", since)
        if not events:
            # No new events: periodic re-evaluation so a "wait" decision can't stall forever.
            events = [{"source": "agent", "type": "recheck", "text": "定时复查: 无新事件, 重新评估当前战场态势"}]

        state = requests.get(f"{BRIDGE}/state", timeout=10).json()
        state.pop("markers", None)  # bridge-internal marker mechanics; LLM mistook them for targets once
        context = (
            "## 新事件\n"
            + "\n".join(f"[{e['source']}/{e['type']}] {e['text']}" for e in events)
            + "\n\n## 你此前的决策(最近10条)\n"
            + ("\n".join(history[-10:]) or "(无)")
            + "\n\n## 当前战场快照\n"
            + json.dumps(state, ensure_ascii=False, indent=1)
        )

        decision = call_llm(context)
        reason = decision.get("reason", "")
        actions = decision.get("actions", [])
        print(f"[agent] {reason}")
        stamp = time.strftime("%H:%M:%S")
        if not actions:
            history.append(f"[{stamp}] 不开火: {reason}")
        for action in actions:
            resp = requests.post(f"{BRIDGE}/fire", json=action, timeout=10).json()
            result = resp.get("result")
            print(f"[agent] fire {action} -> {result}")
            history.append(f"[{stamp}] fire {json.dumps(action, ensure_ascii=False)} -> {result}")


if __name__ == "__main__":
    main()
