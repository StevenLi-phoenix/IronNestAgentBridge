using System.Text.RegularExpressions;
using Il2Cpp;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Reads both teleprinters:
///   Primary   = 最高统帅部 wire (High Command mission directives)
///   Secondary = 战场报告 (field reports from spotters, printed on the adjacent machine)
/// Text is captured via Teleprinter.CaptureMissionState().CurrentFullRich — the full
/// printed roll — then diffed so each newly printed message becomes one bridge event.
/// </summary>
public class TeleprinterReader
{
    private static readonly Regex RichTags = new("<[^>]{1,64}?>", RegexOptions.Compiled);

    private readonly Dictionary<Teleprinter.Teleprinters, string> _lastText = new();

    public static string StripRich(string rich)
        => RichTags.Replace(rich ?? "", "").Replace("\r", "").Trim();

    private static Teleprinter? Get(Teleprinter.Teleprinters which)
    {
        try { return Teleprinter.GetTeleprinter(which); }
        catch { return null; }
    }

    public TeleprinterDto Read(Teleprinter.Teleprinters which)
    {
        var printer = Get(which);
        var dto = new TeleprinterDto
        {
            Which = which == Teleprinter.Teleprinters.Primary ? "primary" : "secondary",
            Bound = printer != null,
        };
        if (printer == null)
            return dto;
        try
        {
            var save = printer.CaptureMissionState();
            dto.FullText = StripRich(save?.CurrentFullRich ?? "");
        }
        catch
        {
            dto.Bound = false;
        }
        return dto;
    }

    public List<TeleprinterDto> ReadAll() => new()
    {
        Read(Teleprinter.Teleprinters.Primary),
        Read(Teleprinter.Teleprinters.Secondary),
    };

    public void Reset() => _lastText.Clear();

    /// <summary>Poll both printers and emit an event for any newly printed text.</summary>
    public void PollAndEmitEvents()
    {
        foreach (var which in new[] { Teleprinter.Teleprinters.Primary, Teleprinter.Teleprinters.Secondary })
        {
            var dto = Read(which);
            if (!dto.Bound) continue;

            var text = dto.FullText;
            _lastText.TryGetValue(which, out var last);
            last ??= "";

            if (text == last || text.Length == 0)
                continue;

            string delta;
            if (text.Length > last.Length && text.StartsWith(last, StringComparison.Ordinal))
                delta = text[last.Length..].Trim();
            else if (text.Length > last.Length && text.EndsWith(last, StringComparison.Ordinal))
                delta = text[..(text.Length - last.Length)].Trim();
            else
                delta = text; // roll was cleared/replaced — send the whole thing

            _lastText[which] = text;
            if (delta.Length > 0)
            {
                var source = which == Teleprinter.Teleprinters.Primary ? "primary" : "secondary";
                EventLog.Append("telegraph_message", source, delta);
            }
        }
    }

    /// <summary>Print lines on a teleprinter (e.g. LLM acknowledgment back to the operator).</summary>
    public bool Print(Teleprinter.Teleprinters which, IEnumerable<string> lines)
    {
        var printer = Get(which);
        if (printer == null) return false;
        var il2cppLines = new Il2CppSystem.Collections.Generic.List<string>();
        foreach (var line in lines)
            il2cppLines.Add(line);
        printer.SubmitLines("AgentBridge", il2cppLines.Cast<Il2CppSystem.Collections.Generic.IEnumerable<string>>());
        printer.TryStart(ignoreInitialDelay: true);
        return true;
    }
}
