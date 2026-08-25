using IronNestAgentBridge.Agent;
using UnityEngine;

namespace IronNestAgentBridge.Ui;

/// <summary>
/// FCS-style IMGUI HUD: pure GUI.Box + GUI.Label with hand-laid rects. This game's IL2CPP
/// build strips the entire GUILayout family ("Method unstripping failed"), so no layout,
/// no scroll views. GUI.Button is probed once at runtime and disabled if stripped —
/// hotkeys (F10 panel, F11 LLM control, F12 priority queue, F9 full reset) always work.
/// </summary>
public class AgentWindow
{
    public bool Visible = true;

    private const float Y = 40f;
    private const float W = 470f;
    private static float X => Screen.width - W - 20f; // top-right, away from the FCS HUD
    private const float LineH = 19f;
    private const int WrapChars = 52;

    private bool _buttonsBroken;

    private bool Button(Rect rect, string label)
    {
        if (_buttonsBroken) return false;
        try
        {
            return GUI.Button(rect, label);
        }
        catch (Exception)
        {
            _buttonsBroken = true; // stripped from the game build; hotkeys take over
            return false;
        }
    }

    private static IEnumerable<string> Wrap(string text, int maxLines, bool fromEnd = false)
    {
        var raw = (text ?? "").Replace("\r", "").Split('\n');
        var lines = new List<string>();
        foreach (var line in raw)
        {
            if (line.Length == 0) { lines.Add(""); continue; }
            for (var i = 0; i < line.Length; i += WrapChars)
                lines.Add(line.Substring(i, Math.Min(WrapChars, line.Length - i)));
        }
        if (lines.Count <= maxLines)
            return lines;
        return fromEnd ? lines.TakeLast(maxLines) : lines.Take(maxLines);
    }

    public void Draw(FdoAgent agent, AgentBridgeMod mod)
    {
        if (!Visible) return;

        // First pass: compose all lines, then size the box to fit.
        var lines = new List<(string text, Color? color)>();
        void Add(string text, Color? color = null) => lines.Add((text, color));

        var running = agent.IsRunning;
        var (stateText, stateColor) = agent.State switch
        {
            FdoAgent.AgentState.Running => ("● RUNNING", Color.green),
            FdoAgent.AgentState.Paused => ("● PAUSED", Color.yellow),
            FdoAgent.AgentState.Stopping => ("● STOPPING", new Color(1f, 0.55f, 0f)),
            _ => ("● STOPPED", Color.red),
        };
        Add(stateText + $"  {AgentConfig.Model}", stateColor);
        Add($"状态: {agent.Status}");
        Add(UsageMeter.Summary);
        Add($"context: {UsageMeter.LastPromptTokens:N0} tokens");

        foreach (var l in (mod.LastFcsSummary ?? "").Split('\n'))
            if (l.Length > 0)
                Add(l);

        // Line budget from the 80%-of-screen height cap: fixed head first, a reserved tail
        // for tools/log, and the streaming/decision text gets everything left over.
        const float buttonRowH = 26f;
        var maxHeight = Screen.height * 0.8f;
        var maxTotalLines = Math.Max(14, (int)((maxHeight - 30f - buttonRowH - 10f) / LineH));
        const int toolBudget = 3;
        const int logBudget = 12;
        var reservedTail = toolBudget + 1 + logBudget + 1; // tools + log header + log + hint row
        var textBudget = Math.Max(8, maxTotalLines - lines.Count - reservedTail - 1);

        if (agent.IsStreaming)
        {
            Add("—— 思考中 ▌ ——", Color.cyan);
            foreach (var l in Wrap(agent.StreamingText, textBudget, fromEnd: true))
                Add(l, Color.cyan);
        }
        else if (agent.LastReason.Length > 0)
        {
            Add("—— 最新决策 ——", Color.yellow);
            foreach (var l in Wrap(agent.LastReason, Math.Min(textBudget, 14)))
                Add(l, Color.yellow);
        }

        var tools = agent.RecentToolCalls();
        foreach (var t in tools.TakeLast(toolBudget))
            Add("🔧 " + (t.Length > WrapChars ? t[..WrapChars] + "…" : t));

        var log = agent.LogSnapshot();
        if (log.Count > 0)
        {
            Add("—— 日志 ——");
            foreach (var entry in log.TakeLast(logBudget))
                Add(entry.Length > WrapChars + 10 ? entry[..(WrapChars + 10)] + "…" : entry);
        }

        if (_buttonsBroken)
            Add("按钮被游戏裁剪: F11=LLM开关 F9=全重置", Color.gray);

        if (lines.Count > maxTotalLines)
            lines.RemoveRange(maxTotalLines, lines.Count - maxTotalLines);

        var height = Math.Min(30f + buttonRowH + lines.Count * LineH + 10f, maxHeight);
        var box = new Rect(X, Y, W, height);
        GUI.Box(box, "IronNest Agent Bridge  [F10]");

        var y = box.y + 24f;

        // Button row (probed; silently absent when stripped).
        if (!_buttonsBroken)
        {
            if (Button(new Rect(box.x + 10f, y, 110f, 22f), running ? "停止 LLM" : "启动 LLM"))
                mod.ToggleLlmControl();
            if (Button(new Rect(box.x + 126f, y, 90f, 22f), "全重置"))
                mod.FullReset("panel button");
        }
        y += buttonRowH;

        var prevColor = GUI.color;
        foreach (var (text, color) in lines)
        {
            GUI.color = color ?? Color.white;
            GUI.Label(new Rect(box.x + 10f, y, W - 20f, LineH + 2f), text);
            y += LineH;
        }
        GUI.color = prevColor;
    }
}
