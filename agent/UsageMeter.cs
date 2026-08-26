using System.Globalization;

namespace IronNestAgentBridge.Agent;

/// <summary>
/// Session token and cost accounting. Accumulates for the life of the process unless
/// <see cref="Reset"/> is called.
///
/// Writers are the agent background thread (through the stream parser), readers are the Unity
/// main thread drawing the panel every frame — so every field is read and written under one
/// cheap, non-reentrant lock.
/// </summary>
public static class UsageMeter
{
    private static readonly object Lock = new();

    private static long _promptTokens;
    private static long _completionTokens;
    private static long _cacheHitTokens;
    private static long _cacheMissTokens;
    private static long _rounds;
    private static long _lastPromptTokens;
    private static double _estimatedCost;

    public static long PromptTokens { get { lock (Lock) return _promptTokens; } }
    public static long CompletionTokens { get { lock (Lock) return _completionTokens; } }
    public static long CacheHitTokens { get { lock (Lock) return _cacheHitTokens; } }
    public static long CacheMissTokens { get { lock (Lock) return _cacheMissTokens; } }

    /// <summary>One count per HTTP request, not per usage frame.</summary>
    public static long Rounds { get { lock (Lock) return _rounds; } }

    /// <summary>
    /// Prompt tokens of the most recent round, i.e. the current context occupancy. The agent's
    /// auto-compact threshold (400_000) reads this; note it lags one round behind.
    /// </summary>
    public static long LastPromptTokens { get { lock (Lock) return _lastPromptTokens; } }

    public static double EstimatedCost { get { lock (Lock) return _estimatedCost; } }

    /// <summary>
    /// Off-peak is [00:30, 08:30) Beijing time (UTC+8); the whole price list is halved there.
    /// </summary>
    public static bool IsOffPeak
    {
        get
        {
            var beijing = DateTime.UtcNow.AddHours(8).TimeOfDay;
            return beijing >= new TimeSpan(0, 30, 0) && beijing < new TimeSpan(8, 30, 0);
        }
    }

    /// <summary>Ready-made panel line; the UI displays it as-is.</summary>
    public static string Summary
    {
        get
        {
            string text;
            lock (Lock)
            {
                text = string.Format(CultureInfo.InvariantCulture,
                    "tokens: in {0:N0} (cache hit {1:N0}) out {2:N0} · {3} rounds · ≈{4:F3} {5}",
                    _promptTokens, _cacheHitTokens, _completionTokens, _rounds, _estimatedCost,
                    AgentConfig.PriceCurrency);
            }
            return IsOffPeak ? text + " (谷时半价)" : text;
        }
    }

    /// <summary>
    /// Records one completed request. Call exactly once per HTTP request, with the values of
    /// the last usage frame that request delivered — the provider may emit several.
    ///
    /// When the provider reported no cache breakdown at all (a non-DeepSeek endpoint), the
    /// whole prompt is charged conservatively at the cache-miss rate.
    /// </summary>
    public static void AddRound(long promptTokens, long completionTokens, long cacheHitTokens, long cacheMissTokens)
    {
        var offPeak = IsOffPeak;
        var factor = offPeak ? 0.5 : 1.0;

        double roundCost;
        double totalCost;

        lock (Lock)
        {
            _promptTokens += promptTokens;
            _completionTokens += completionTokens;
            _cacheHitTokens += cacheHitTokens;
            _cacheMissTokens += cacheMissTokens;
            _rounds++;
            _lastPromptTokens = promptTokens;

            var input = cacheHitTokens + cacheMissTokens > 0
                ? cacheHitTokens / 1e6 * AgentConfig.PriceInputCacheHit
                  + cacheMissTokens / 1e6 * AgentConfig.PriceInputCacheMiss
                : promptTokens / 1e6 * AgentConfig.PriceInputCacheMiss;

            roundCost = factor * (input + completionTokens / 1e6 * AgentConfig.PriceOutput);
            _estimatedCost += roundCost;
            totalCost = _estimatedCost;
        }

        // Written outside the lock: file IO must not be held against the UI thread's readers.
        TransactionLog.Write("usage",
            $"round: in={promptTokens} (hit {cacheHitTokens}/miss {cacheMissTokens}) out={completionTokens} {(offPeak ? "off-peak" : "peak")}",
            new
            {
                prompt = promptTokens,
                completion = completionTokens,
                cacheHit = cacheHitTokens,
                cacheMiss = cacheMissTokens,
                roundCost,
                totalCost,
            });
    }

    /// <summary>
    /// Zeroes the session meter. Called when the agent starts a fresh conversation, so the
    /// displayed context size and cost belong to the dialogue actually in flight.
    /// </summary>
    public static void Reset()
    {
        lock (Lock)
        {
            _promptTokens = 0;
            _completionTokens = 0;
            _cacheHitTokens = 0;
            _cacheMissTokens = 0;
            _rounds = 0;
            _lastPromptTokens = 0;
            _estimatedCost = 0;
        }
    }
}
