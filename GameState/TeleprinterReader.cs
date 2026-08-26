using System.Text.RegularExpressions;
using Il2Cpp;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// The two teleprinters. Primary carries High Command's mission orders, Secondary carries
/// battlefield reports from observers. Stateful: each machine's last full roll is kept so the
/// poller can emit only what is new.
///
/// Independent of map binding — orders arrive before the tactical map is ready.
/// </summary>
public sealed class TeleprinterReader
{
    /// <summary>Wire values of <see cref="TeleprinterDto.Which"/>; also the event source names.</summary>
    public const string PrimaryName = "primary";
    public const string SecondaryName = "secondary";

    /// <summary>Author stamped on every line the bridge prints back.</summary>
    public const string PrintAuthor = "AgentBridge";

    /// <summary>Seconds between roll diffs. Independent of map binding. Driven by the mod.</summary>
    public const float TelegraphPollSeconds = 1.0f;

    /// <summary>
    /// Rich-text tag stripper. The bounded {1,64} repetition is deliberate: an unbounded
    /// <c>&lt;[^&gt;]*&gt;</c> would happily eat a stray angle bracket in a real message.
    /// </summary>
    private static readonly Regex RichTag = new("<[^>]{1,64}?>", RegexOptions.Compiled);

    private readonly Dictionary<Teleprinter.Teleprinters, string> _last = new();

    /// <summary>Reads one machine's whole roll with rich text removed.</summary>
    public TeleprinterDto Read(Teleprinter.Teleprinters which)
    {
        var dto = new TeleprinterDto
        {
            Which = which == Teleprinter.Teleprinters.Primary ? PrimaryName : SecondaryName,
        };

        var printer = Il2CppSafe.GetRef(() => Teleprinter.GetTeleprinter(which));
        if (printer == null) return dto;

        dto.Bound = true;
        try
        {
            var state = printer.CaptureMissionState();
            dto.FullText = StripRich(state == null ? "" : state.CurrentFullRich ?? "");
        }
        catch
        {
            // The machine exists but will not give up its roll: report it as unbound rather than
            // failing the whole snapshot.
            dto.Bound = false;
        }

        return dto;
    }

    /// <summary>Fixed order [Primary, Secondary].</summary>
    public List<TeleprinterDto> ReadAll() => new()
    {
        Read(Teleprinter.Teleprinters.Primary),
        Read(Teleprinter.Teleprinters.Secondary),
    };

    /// <summary>
    /// Diffs both rolls and emits the new text. The event source is the machine name, not "map" —
    /// the authority of a message depends on which printer produced it.
    /// </summary>
    public void PollAndEmitEvents()
    {
        PollOne(Teleprinter.Teleprinters.Primary, PrimaryName);
        PollOne(Teleprinter.Teleprinters.Secondary, SecondaryName);
    }

    private void PollOne(Teleprinter.Teleprinters which, string source)
    {
        var dto = Read(which);
        if (!dto.Bound) return;

        var text = dto.FullText;
        _last.TryGetValue(which, out var last);
        last ??= "";

        if (text.Length == 0 || string.Equals(text, last, StringComparison.Ordinal)) return;

        string delta;
        if (text.Length > last.Length && text.StartsWith(last, StringComparison.Ordinal))
        {
            // Normal case: the machine typed more lines at the bottom.
            delta = text[last.Length..].Trim();
        }
        else if (text.Length > last.Length && text.EndsWith(last, StringComparison.Ordinal))
        {
            // Never observed in play; kept because prepend printing is a supported print order
            // and silently mis-diffing an order is worse than a redundant branch.
            delta = text[..(text.Length - last.Length)].Trim();
        }
        else
        {
            // The roll was cleared or rewritten: resend the whole thing.
            delta = text;
        }

        // Advance the cursor whether or not anything is emitted, so a whitespace-only delta is
        // not re-diffed forever.
        _last[which] = text;

        if (delta.Length > 0) EventLog.Append("telegraph_message", source, delta);
    }

    /// <summary>
    /// Forgets both rolls. On the next poll each machine's entire roll is re-emitted as new —
    /// intentional: a restarted agent rebuilds its picture from live reality instead of replaying
    /// stale history.
    /// </summary>
    public void Reset() => _last.Clear();

    /// <summary>
    /// Prints lines back onto a machine. Anything other than "primary" (case-insensitive) goes to
    /// the battlefield-report machine, which is where a fire direction officer's traffic belongs.
    /// Stateless, hence static.
    /// </summary>
    public static bool Print(string which, IEnumerable<string> lines)
    {
        var target = string.Equals(which, PrimaryName, StringComparison.OrdinalIgnoreCase)
            ? Teleprinter.Teleprinters.Primary
            : Teleprinter.Teleprinters.Secondary;

        var printer = Il2CppSafe.GetRef(() => Teleprinter.GetTeleprinter(target));
        if (printer == null) return false;

        try
        {
            var payload = new Il2CppSystem.Collections.Generic.List<string>();
            foreach (var line in lines) payload.Add(line ?? "");

            printer.SubmitLines(PrintAuthor,
                payload.TryCast<Il2CppSystem.Collections.Generic.IEnumerable<string>>());
            printer.TryStart(ignoreInitialDelay: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes rich-text tags, carriage returns and surrounding whitespace.</summary>
    private static string StripRich(string rich)
        => RichTag.Replace(rich, "").Replace("\r", "").Trim();
}
