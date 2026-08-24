using System.Text.Json;
using MelonLoader.Utils;

namespace IronNestAgentBridge.Agent;

/// <summary>
/// Durable JSONL transaction log: decisions, staged/dispatched missions, results, errors.
/// One file per day under UserData\IronNestAgentBridge\. Flushed per line so a crash
/// loses nothing; thread-safe (agent thread + main thread both write).
/// </summary>
public static class TransactionLog
{
    private static readonly object Gate = new();
    private static string? _dir;

    private static string Dir
    {
        get
        {
            if (_dir == null)
            {
                _dir = Path.Combine(MelonEnvironment.UserDataDirectory, "IronNestAgentBridge");
                Directory.CreateDirectory(_dir);
            }
            return _dir;
        }
    }

    public static void Write(string type, string text, object? data = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                type,
                text,
                data,
            });
            lock (Gate)
                File.AppendAllText(Path.Combine(Dir, $"transactions-{DateTime.Now:yyyyMMdd}.jsonl"), line + Environment.NewLine);
        }
        catch { /* logging must never break the agent */ }
    }
}
