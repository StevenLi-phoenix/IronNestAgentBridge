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

你的职责是战术决策: 打谁、用什么弹、什么顺序。执行(装填/瞄准/击发)由FCS自动完成。
规则:
- 遵守统帅部电文中的弹药限制与优先目标指令
- 注意目标护甲与免疫弹种(ImmuneShells), 地下工事需AP系
- 反炮兵威胁下优先高价值目标
- 每次决策输出JSON: {"actions": [{"entityId": "...", "shell": "HE"}], "reason": "..."}
  不开火时输出 {"actions": [], "reason": "..."}
"""


def call_llm(context: str) -> dict:
    """Replace with your provider of choice."""
    import anthropic

    client = anthropic.Anthropic()  # ANTHROPIC_API_KEY from env
    msg = client.messages.create(
        model="claude-sonnet-5",
        max_tokens=1024,
        system=SYSTEM_PROMPT,
        messages=[{"role": "user", "content": context}],
    )
    text = msg.content[0].text
    start, end = text.find("{"), text.rfind("}")
    return json.loads(text[start : end + 1]) if start >= 0 else {"actions": []}


def main() -> None:
    since = 0
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
            continue

        state = requests.get(f"{BRIDGE}/state", timeout=10).json()
        context = (
            "## 新事件\n"
            + "\n".join(f"[{e['source']}/{e['type']}] {e['text']}" for e in events)
            + "\n\n## 当前战场快照\n"
            + json.dumps(state, ensure_ascii=False, indent=1)
        )

        decision = call_llm(context)
        print(f"[agent] {decision.get('reason', '')}")
        for action in decision.get("actions", []):
            resp = requests.post(f"{BRIDGE}/fire", json=action, timeout=10).json()
            print(f"[agent] fire {action} -> {resp.get('result')}")


if __name__ == "__main__":
    main()
