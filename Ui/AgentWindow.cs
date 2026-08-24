using IronNestAgentBridge.Agent;
using UnityEngine;

namespace IronNestAgentBridge.Ui;

/// <summary>
/// In-game IMGUI panel (FCS-HUD style). F10 toggles visibility.
/// Shows agent status, last decision, action log; start/stop without leaving the game.
/// </summary>
public class AgentWindow
{
    private const int WindowId = 0x1B57;

    public bool Visible = true;

    private Rect _rect = new(20f, 220f, 460f, 480f);
    private Vector2 _scroll;
    private Vector2 _streamScroll;

    public void Draw(FdoAgent agent, AgentBridgeMod mod)
    {
        if (!Visible) return;
        // This game's interop is missing the GUILayout.Window ctor overload, so draw a
        // fixed panel with Box + BeginArea instead of a managed window.
        GUI.Box(_rect, "IronNest Agent Bridge  [F10]");
        GUILayout.BeginArea(new Rect(_rect.x + 8f, _rect.y + 24f, _rect.width - 16f, _rect.height - 32f));
        try { Body(agent, mod); }
        finally { GUILayout.EndArea(); }
    }

    private void Body(FdoAgent agent, AgentBridgeMod mod)
    {
        GUILayout.BeginHorizontal();
        var running = agent.IsRunning;
        GUI.color = running ? Color.green : Color.red;
        GUILayout.Label(running ? "● RUNNING" : "● STOPPED", GUILayout.Width(90f));
        GUI.color = Color.white;
        GUILayout.Label($"{AgentConfig.Model}", GUILayout.ExpandWidth(true));
        if (GUILayout.Button(running ? "Stop" : "Start", GUILayout.Width(70f)))
        {
            if (running) agent.Stop();
            else agent.Start();
        }
        if (GUILayout.Button("Clear", GUILayout.Width(60f)))
            agent.ClearLog();
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        var llmControl = GUILayout.Toggle(AgentConfig.LlmControl, " LLM控制", GUILayout.Width(110f));
        if (llmControl != AgentConfig.LlmControl)
        {
            AgentConfig.LlmControl = llmControl;
            if (!llmControl && agent.IsRunning) agent.Stop();
            if (llmControl && !agent.IsRunning) agent.Start();
        }
        var pq = GUILayout.Toggle(AgentConfig.PriorityQueue, " 优先队列", GUILayout.Width(110f));
        if (pq != AgentConfig.PriorityQueue)
            AgentConfig.PriorityQueue = pq;
        GUILayout.Label($"staged: {mod.MissionQueue.Count}");
        GUILayout.EndHorizontal();

        GUILayout.Label($"状态: {agent.Status}");

        var stagedList = mod.MissionQueue.Describe();
        if (stagedList.Count > 0)
        {
            GUILayout.Label("优先队列 (前5):");
            foreach (var entry in stagedList.Take(5))
                GUILayout.Label("  " + entry);
        }

        var fcs = mod.LastFcsSummary;
        if (fcs.Length > 0)
            GUILayout.Label(fcs);

        GUILayout.Label(Agent.UsageMeter.Summary);
        GUILayout.Label($"context (last round): {Agent.UsageMeter.LastPromptTokens:N0} tokens");

        var tools = agent.RecentToolCalls();
        if (tools.Count > 0)
        {
            GUILayout.Label("最近工具调用:");
            foreach (var t in tools.TakeLast(4))
                GUILayout.Label("  🔧 " + t);
        }

        GUI.skin.label.wordWrap = true;

        if (agent.IsStreaming || agent.StreamingText.Length > 0 && agent.LastReason.Length == 0)
        {
            GUILayout.Label(agent.IsStreaming ? "思考中 ▌" : "思考流:");
            _streamScroll = GUILayout.BeginScrollView(_streamScroll, GUI.skin.box,
                GUILayout.Height(150f), GUILayout.ExpandWidth(true));
            var text = agent.StreamingText;
            if (text.Length > 4000)
                text = "…" + text[^4000..];
            GUILayout.Label(text);
            GUILayout.EndScrollView();
            if (agent.IsStreaming)
                _streamScroll.y = float.MaxValue; // stick to the newest output
        }
        else if (agent.LastReason.Length > 0)
        {
            GUILayout.Label("最新决策:");
            GUILayout.Box(agent.LastReason, GUILayout.ExpandWidth(true));
        }

        GUILayout.Label("日志:");
        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandHeight(true));
        var log = agent.LogSnapshot();
        for (var i = log.Count - 1; i >= 0; i--)
            GUILayout.Label(log[i]);
        GUILayout.EndScrollView();
    }
}
