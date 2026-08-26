using System.Globalization;
using IronNestAgentBridge.Agent;
using UnityEngine;

namespace IronNestAgentBridge.Ui;

/// <summary>
/// The mod's only human interface: a read-only IMGUI status panel in the top right corner.
///
/// Rendering constraints (hard, imposed by the game's IL2CPP build):
/// <list type="bullet">
/// <item>The whole <c>GUILayout</c> family is stripped ("Method unstripping failed"), including
/// <c>GUILayout.Window</c>, <c>BeginArea</c> and the scroll views. Only <c>GUI.Box</c> and
/// <c>GUI.Label</c> on hand-computed rects are legal here.</item>
/// <item><c>GUI.Button</c> may be stripped too, so it is probed at runtime through
/// <see cref="Button"/>: the first throw disables the button row forever and the panel falls
/// back to hotkeys, which are the authoritative control path anyway
/// (F10 panel, F11 LLM master switch, F9 full reset).</item>
/// </list>
///
/// Drawing is pure: OnGUI runs several times per frame, so nothing here may advance state.
/// The only side effects allowed are the two button callbacks and the stripping probe flag.
/// </summary>
public static class AgentWindow
{
    /// <summary>Panel visibility, toggled by F10. Not persisted — every process starts shown.</summary>
    public static bool Visible = true;

    /// <summary>Set once <c>GUI.Button</c> is proven to be stripped; never reset.</summary>
    private static bool _buttonsBroken;

    private const float Y = 40f;
    private const float W = 470f;

    /// <summary>Right-edge gap. The panel hugs the right side to stay clear of the FCS HUD.</summary>
    private const float EdgeMargin = 20f;

    private const float LineH = 19f;
    private const float ButtonRowH = 26f;

    /// <summary>
    /// Wrap budget in display columns, not chars: CJK counts 2, so a full line of Chinese stays
    /// inside the label's 450px instead of being clipped by IMGUI.
    /// </summary>
    private const int WrapChars = 52;

    /// <summary>Log lines are only truncated, never wrapped, so they get a wider budget.</summary>
    private const int LogChars = WrapChars + 10;

    private const int ToolBudget = 3;
    private const int LogBudget = 12;

    /// <summary>Ceiling on panel height as a fraction of the screen.</summary>
    private const float ScreenFraction = 0.8f;

    /// <summary>One rendered line plus its colour. Colour is explicit; white is the default.</summary>
    private readonly struct Line
    {
        public readonly string Text;
        public readonly Color Color;

        public Line(string text, Color color)
        {
            Text = text;
            Color = color;
        }
    }

    /// <summary>
    /// Draws the whole panel. Callers must already have checked that the agent exists, the map
    /// is bound and no cinematic is playing — the HUD stays invisible otherwise.
    /// Guaranteed not to throw: a stripped IMGUI member must not flood the frame loop.
    /// </summary>
    public static void Draw(FdoAgent agent, AgentBridgeMod mod)
    {
        if (!Visible) return;

        try { DrawPanel(agent, mod); }
        catch { /* stripped member or transient Il2Cpp fault: skip this frame silently */ }
    }

    private static void DrawPanel(FdoAgent agent, AgentBridgeMod mod)
    {
        // Two passes: assemble every body line first, then size the box to fit them. The panel
        // height follows its content and is capped at 80% of the screen.
        var maxHeight = Screen.height * ScreenFraction;
        var maxTotalLines = Math.Max(14, (int)((maxHeight - 30f - ButtonRowH - 10f) / LineH));

        var lines = new List<Line>();
        AppendHeader(lines, agent, mod);

        // Line budget for the thinking/decision block: whatever is left once the fixed header
        // and a full-size tail (tools + log header + log + hint) are accounted for.
        var reservedTail = ToolBudget + 1 + LogBudget + 1;
        var textBudget = Math.Max(8, maxTotalLines - lines.Count - reservedTail - 1);

        AppendThinking(lines, agent, textBudget);
        AppendToolCalls(lines, agent);
        AppendLog(lines, agent);

        if (_buttonsBroken)
        {
            lines.Add(new Line("按钮被游戏裁剪: F11=LLM开关 F9=全重置", Color.gray));
        }

        // Hard cut: losing the tail of the log beats growing past the screen budget.
        if (lines.Count > maxTotalLines) lines.RemoveRange(maxTotalLines, lines.Count - maxTotalLines);

        var height = Math.Min(30f + ButtonRowH + lines.Count * LineH + 10f, maxHeight);

        // X is recomputed every frame so the panel follows resolution changes.
        var box = new Rect(Screen.width - W - EdgeMargin, Y, W, height);
        GUI.Box(box, "IronNest Agent Bridge  [F10]");

        var y = box.y + 24f;
        DrawButtonRow(box, y, agent, mod);

        // The row's height is consumed whether or not buttons were drawn, so the body starts at
        // the same place in both cases.
        y += ButtonRowH;

        // GUI.color is global state; leaking a tint from here would repaint the game's own UI.
        var previousColor = GUI.color;
        try
        {
            foreach (var line in lines)
            {
                GUI.color = line.Color;
                GUI.Label(new Rect(box.x + 10f, y, W - 20f, LineH + 2f), line.Text);
                y += LineH;
            }
        }
        finally
        {
            GUI.color = previousColor;
        }
    }

    /// <summary>Fixed head of the panel: state, status, metering and the FCS summary.</summary>
    private static void AppendHeader(List<Line> lines, FdoAgent agent, AgentBridgeMod mod)
    {
        var (stateText, stateColor) = agent.State switch
        {
            FdoAgent.AgentState.Running => ("● RUNNING", Color.green),
            FdoAgent.AgentState.Paused => ("● PAUSED", Color.yellow),
            FdoAgent.AgentState.Stopping => ("● STOPPING", new Color(1f, 0.55f, 0f)),
            _ => ("● STOPPED", Color.red),
        };

        lines.Add(new Line(stateText + "  " + AgentConfig.Model, stateColor));
        lines.Add(White($"状态: {agent.Status}"));

        // Pre-formatted by the meter; the panel never re-lays it out.
        lines.Add(White(UsageMeter.Summary));
        lines.Add(White("context: "
            + UsageMeter.LastPromptTokens.ToString("N0", CultureInfo.InvariantCulture)
            + " tokens"));

        // Opaque pass-through: the mod owns the wording (queue counts plus the T9(左)/T10(右)
        // gun lines). The panel only splits it into rows.
        var fcs = mod.LastFcsSummary;
        if (!string.IsNullOrEmpty(fcs))
        {
            foreach (var segment in fcs.Split('\n'))
            {
                if (segment.Length > 0) lines.Add(White(segment));
            }
        }
    }

    /// <summary>
    /// Live stream if the agent is talking, otherwise the last decision. The stream is shown
    /// tail-first (the newest output is what matters); a finished decision is shown head-first
    /// (the conclusion opens it) and capped at 14 lines.
    /// </summary>
    private static void AppendThinking(List<Line> lines, FdoAgent agent, int textBudget)
    {
        if (agent.IsStreaming)
        {
            lines.Add(new Line("—— 思考中 ▌ ——", Color.cyan));
            foreach (var text in Wrap(agent.StreamingText, textBudget, fromEnd: true))
            {
                lines.Add(new Line(text, Color.cyan));
            }
            return;
        }

        if (string.IsNullOrEmpty(agent.LastReason)) return;

        lines.Add(new Line("—— 最新决策 ——", Color.yellow));
        foreach (var text in Wrap(agent.LastReason, Math.Min(textBudget, 14)))
        {
            lines.Add(new Line(text, Color.yellow));
        }
    }

    private static void AppendToolCalls(List<Line> lines, FdoAgent agent)
    {
        var calls = agent.RecentToolCalls();
        if (calls == null) return;

        foreach (var call in calls.TakeLast(ToolBudget))
        {
            lines.Add(White("🔧 " + Truncate(call, WrapChars)));
        }
    }

    private static void AppendLog(List<Line> lines, FdoAgent agent)
    {
        var log = agent.LogSnapshot();
        if (log == null || log.Count == 0) return;

        lines.Add(White("—— 日志 ——"));
        foreach (var entry in log.TakeLast(LogBudget))
        {
            lines.Add(White(Truncate(entry, LogChars)));
        }
    }

    /// <summary>
    /// Both labels follow <c>agent.State</c>, the same source as the status dot, so the dot and
    /// the button can never disagree. Anything but Stopped offers to stop.
    /// </summary>
    private static void DrawButtonRow(Rect box, float y, FdoAgent agent, AgentBridgeMod mod)
    {
        if (_buttonsBroken) return;

        var toggleLabel = agent.State == FdoAgent.AgentState.Stopped ? "启动 LLM" : "停止 LLM";

        if (Button(new Rect(box.x + 10f, y, 110f, 22f), toggleLabel)) mod.ToggleLlmControl();
        if (Button(new Rect(box.x + 126f, y, 90f, 22f), "全重置")) mod.FullReset("panel button");
    }

    /// <summary>
    /// Runtime probe for <c>GUI.Button</c>. The first failure is swallowed and latched: from
    /// then on nothing is drawn and every call reports "not clicked".
    /// </summary>
    private static bool Button(Rect rect, string label)
    {
        if (_buttonsBroken) return false;

        try { return GUI.Button(rect, label); }
        catch
        {
            _buttonsBroken = true;
            return false;
        }
    }

    private static Line White(string text) => new(text, Color.white);

    /// <summary>
    /// Hard-wraps text to <see cref="WrapChars"/> display columns. Newlines are normalised
    /// (CRLF and LF alike) and empty lines are kept, so paragraph spacing survives; no word or
    /// punctuation boundaries are honoured. When the result exceeds <paramref name="maxLines"/>
    /// either the tail (<paramref name="fromEnd"/>) or the head is returned.
    /// </summary>
    private static List<string> Wrap(string? text, int maxLines, bool fromEnd = false)
    {
        var wrapped = new List<string>();
        if (maxLines <= 0) return wrapped;

        foreach (var paragraph in (text ?? "").Replace("\r", "").Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                wrapped.Add("");
                continue;
            }

            var start = 0;
            while (start < paragraph.Length)
            {
                var take = SpanForWidth(paragraph, start, WrapChars);
                wrapped.Add(paragraph.Substring(start, take));
                start += take;
            }
        }

        if (wrapped.Count <= maxLines) return wrapped;

        return fromEnd
            ? wrapped.GetRange(wrapped.Count - maxLines, maxLines)
            : wrapped.GetRange(0, maxLines);
    }

    /// <summary>
    /// Cuts <paramref name="text"/> to <paramref name="budget"/> display columns, marking the
    /// cut with an ellipsis. Text that already fits is returned untouched.
    /// </summary>
    private static string Truncate(string? text, int budget)
    {
        var body = text ?? "";
        var take = SpanForWidth(body, 0, budget);
        return take >= body.Length ? body : body.Substring(0, take) + "…";
    }

    /// <summary>
    /// Number of chars from <paramref name="start"/> that fit into <paramref name="budget"/>
    /// display columns. Surrogate pairs are never split, and at least one code point is always
    /// consumed so callers cannot spin.
    /// </summary>
    private static int SpanForWidth(string text, int start, int budget)
    {
        var used = 0;
        var i = start;

        while (i < text.Length)
        {
            var step = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                ? 2
                : 1;
            var codePoint = step == 2 ? char.ConvertToUtf32(text, i) : text[i];
            var width = IsWide(codePoint) ? 2 : 1;

            if (used + width > budget && i > start) break;

            used += width;
            i += step;
        }

        return i - start;
    }

    /// <summary>
    /// East Asian Wide / Fullwidth plus the emoji planes — everything that renders roughly
    /// double width in the panel font. Ambiguous-width characters the panel itself uses
    /// (●, ▌, ——) deliberately count as one, matching how the game font draws them.
    /// </summary>
    private static bool IsWide(int codePoint) =>
        (codePoint >= 0x1100 && codePoint <= 0x115F) ||     // Hangul Jamo
        (codePoint >= 0x2E80 && codePoint <= 0x303E) ||     // CJK radicals, CJK punctuation
        (codePoint >= 0x3041 && codePoint <= 0x33FF) ||     // kana, Hangul compat, CJK compat
        (codePoint >= 0x3400 && codePoint <= 0x4DBF) ||     // CJK extension A
        (codePoint >= 0x4E00 && codePoint <= 0x9FFF) ||     // CJK unified ideographs
        (codePoint >= 0xA000 && codePoint <= 0xA4CF) ||     // Yi
        (codePoint >= 0xAC00 && codePoint <= 0xD7A3) ||     // Hangul syllables
        (codePoint >= 0xF900 && codePoint <= 0xFAFF) ||     // CJK compatibility ideographs
        (codePoint >= 0xFE10 && codePoint <= 0xFE19) ||     // vertical forms
        (codePoint >= 0xFE30 && codePoint <= 0xFE6F) ||     // CJK compatibility forms
        (codePoint >= 0xFF00 && codePoint <= 0xFF60) ||     // fullwidth forms
        (codePoint >= 0xFFE0 && codePoint <= 0xFFE6) ||     // fullwidth signs
        (codePoint >= 0x1F300 && codePoint <= 0x1FAFF) ||   // emoji, e.g. the tool wrench
        (codePoint >= 0x20000 && codePoint <= 0x3FFFD);     // CJK extension B and beyond
}
