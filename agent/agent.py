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
- 弹种选择: armour=0的目标(步兵/无甲车辆)用HE即可; armour>=1的目标HE大概率"未击穿",
  直接用APHE(兼具穿甲和爆破)或AP。role含Fortification或rawId为supplycash/
  hostilebunker等工事类=地下/加固目标, 必须AP系穿甲弹。immuneShells非空时严禁选名单内弹种
- 反炮兵威胁下优先高价值目标
- 战争迷雾: entities[]是当前唯一的已揭示目标清单, 为空就说明没有任何目标被揭示。
  entityId必须一字不差地取自entities[]里实际存在的id, 严禁凭空猜测或编造id。
  未揭示目标只能根据电报情报三角定位后用bearingDeg+distanceKm盲射
  (方位角以炮塔为原点, 正北=0°顺时针; 距离单位km)。
- 坐标换算: 电文中的网格如"H5 0:9"表示 kmX=字母序号+第一个子格/10+0.05 (A=0,B=1,...,
  H=7, 即kmX≈7.05), kmY=(行号-1)+第二个子格/10+0.05 (即kmY≈4.95)。+0.05是取0.1km子格
  的中心, 不加会系统性偏向西南。快照中的mapX/mapY换算:
  kmX=10.016+mapX*3.8164, kmY=5.235+mapY*3.8164。两点间: dx=kmX2-kmX1, dy=kmY2-kmY1,
  距离=sqrt(dx²+dy²) km, 从点1看点2的方位角=atan2(dx,dy)转成0~360°。
  炮塔自身位置见快照turretMapX/turretMapY(注意先换算成km坐标再参与计算)。
  战场报告给出的"自X的方位角"是从X点出发的观测线, 两条线相交即目标位置;
  "自X距离Y"则是以X为圆心的圆。逐步写出你的计算过程再给出结论。
- 盲射精度认知: 网格±0.05km、报告方位角±0.5°, 在远距离交汇时误差可达数百米。
  因此盲射=效力侦察(ranging fire): 第一发的价值是炸开迷雾揭示目标。
  弹着揭示目标(entity_revealed事件)后, 立即用entityId对其精确补射, 那才是摧毁手段。
  远距离(>8km)斜交线解算尤其不可靠, 若同一目标有"方位角+距离"组合优先用它,
  且优先选距目标近的观测员的数据。
- 每次决策输出JSON, 两种action格式:
  {"actions": [{"entityId": "<必须是entities[]中存在的id>", "shell": "HE"},
               {"bearingDeg": 75.0, "distanceKm": 9.1, "shell": "AP"}], "reason": "..."}
  不开火时输出 {"actions": [], "reason": "..."}
- 队列纪律(最重要): fcs.pendingTasks列出所有待执行任务(若无此字段则以pendingCount计数),
  每个任务执行约需1分钟, 队列会自动逐个打完。目标在pendingTasks/你的决策历史里已有
  未执行完的任务时, 严禁再排——"已下达"不等于"已打完", 你看不到弹着不代表任务丢了。
  补射的唯一条件: 收到该目标明确的未击穿/未命中报告, 且队列中已无针对它的任务。
  已摧毁(isAlive=false)的目标绝不再排。宁可这轮不开火, 也不要堆积队列浪费弹药。
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
            "max_tokens": 393_216,  # deepseek-v4-flash API ceiling
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
