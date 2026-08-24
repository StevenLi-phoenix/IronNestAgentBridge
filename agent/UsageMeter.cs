namespace IronNestAgentBridge.Agent;

/// <summary>Session-cumulative LLM token usage and estimated cost (prices from MelonPreferences, per 1M tokens).</summary>
public static class UsageMeter
{
    private static readonly object Gate = new();

    public static long PromptTokens { get; private set; }
    public static long CompletionTokens { get; private set; }
    public static long CacheHitTokens { get; private set; }
    public static long CacheMissTokens { get; private set; }
    public static int Rounds { get; private set; }

    /// <summary>Prompt size of the most recent LLM round — the live context-window footprint.</summary>
    public static long LastPromptTokens { get; private set; }

    /// <summary>DeepSeek off-peak window: 00:30–08:30 Beijing time (UTC+8), all prices halved.</summary>
    public static bool IsOffPeak
    {
        get
        {
            var beijing = DateTime.UtcNow.AddHours(8).TimeOfDay;
            return beijing >= new TimeSpan(0, 30, 0) && beijing < new TimeSpan(8, 30, 0);
        }
    }

    private static double _cost; // accumulated with the peak/off-peak factor active per round

    public static void AddRound(long prompt, long completion, long cacheHit, long cacheMiss)
    {
        var factor = IsOffPeak ? 0.5 : 1.0;
        var input = cacheHit + cacheMiss > 0
            ? cacheHit / 1e6 * AgentConfig.PriceInputCacheHit + cacheMiss / 1e6 * AgentConfig.PriceInputCacheMiss
            : prompt / 1e6 * AgentConfig.PriceInputCacheMiss;
        var roundCost = factor * (input + completion / 1e6 * AgentConfig.PriceOutput);

        lock (Gate)
        {
            LastPromptTokens = prompt;
            PromptTokens += prompt;
            CompletionTokens += completion;
            CacheHitTokens += cacheHit;
            CacheMissTokens += cacheMiss;
            Rounds++;
            _cost += roundCost;
        }
        TransactionLog.Write("usage",
            $"round: in={prompt} (hit {cacheHit}/miss {cacheMiss}) out={completion} {(factor < 1 ? "off-peak" : "peak")}",
            new { prompt, completion, cacheHit, cacheMiss, roundCost, totalCost = EstimatedCost });
    }

    /// <summary>Accumulated cost in the configured currency, peak/off-peak applied per round.</summary>
    public static double EstimatedCost
    {
        get { lock (Gate) return _cost; }
    }

    public static string Summary
    {
        get
        {
            lock (Gate)
                return $"tokens: in {PromptTokens:N0} (cache hit {CacheHitTokens:N0}) out {CompletionTokens:N0}"
                     + $" · {Rounds} rounds · ≈{EstimatedCost:F3} {AgentConfig.PriceCurrency}"
                     + (IsOffPeak ? " (谷时半价)" : "");
        }
    }
}
