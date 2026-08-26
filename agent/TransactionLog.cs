using System.Text;
using System.Text.Json;
using MelonLoader.Utils;

namespace IronNestAgentBridge.Agent;

/// <summary>
/// Append-only JSONL audit trail shared by the whole mod: decisions, tool calls, usage,
/// requisitions, resets.
///
/// Invariant: the log can never take the agent down. Every failure — full disk, permissions,
/// a bad path — is swallowed.
/// </summary>
public static class TransactionLog
{
    /// <summary>Types in use: usage, compact, tool, decision, fire, cancel, adjust, turret,
    /// agent, mission, reset, requisition, scout_plane.</summary>
    private static readonly object Lock = new();

    // Default escaping keeps the file pure ASCII (non-ASCII becomes \uXXXX), which is what the
    // existing logs look like. The explicit UTF-8 writer below is still required: a Chinese
    // Windows would otherwise default to GBK.
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Writes one line and flushes it. Both the agent background thread and the Unity main
    /// thread call this, so the whole operation is held under one lock.
    /// </summary>
    public static void Write(string type, string text, object? data = null)
    {
        try
        {
            var dir = Path.Combine(MelonEnvironment.UserDataDirectory, "IronNestAgentBridge");

            // Local date, recomputed per write: the file rolls over at midnight without a restart.
            var path = Path.Combine(dir, $"transactions-{DateTime.Now:yyyyMMdd}.jsonl");

            var line = JsonSerializer.Serialize(new
            {
                ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                type,
                text,
                data,
            }, JsonOptions);

            lock (Lock)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(path, line + "\n", Utf8NoBom);
            }
        }
        catch
        {
            // Deliberately silent.
        }
    }
}
