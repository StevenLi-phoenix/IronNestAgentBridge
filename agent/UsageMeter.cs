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

    public static void AddRound(long prompt, long completion, long cacheHit, long cacheMiss)
    {
        lock (Gate)
        {
            LastPromptTokens = prompt;
            PromptTokens += prompt;
            CompletionTokens += completion;
            CacheHitTokens += cacheHit;
            CacheMissTokens += cacheMiss;
            Rounds++;
        }
        TransactionLog.Write("usage",
            $"round: in={prompt} (hit {cacheHit}/miss {cacheMiss}) out={completion}",
            new { prompt, completion, cacheHit, cacheMiss, totalCost = EstimatedCost });
    }

    /// <summary>Cost in the currency of the configured prices. Cache-hit input billed separately when prices set.</summary>
    public static double EstimatedCost
    {
        get
        {
            lock (Gate)
            {
                // If cache split is reported, bill hit/miss separately; otherwise flat input price on prompt.
                var input = CacheHitTokens + CacheMissTokens > 0
                    ? CacheHitTokens / 1e6 * AgentConfig.PriceInputCacheHit
                      + CacheMissTokens / 1e6 * AgentConfig.PriceInputCacheMiss
                    : PromptTokens / 1e6 * AgentConfig.PriceInputCacheMiss;
                return input + CompletionTokens / 1e6 * AgentConfig.PriceOutput;
            }
        }
    }

    public static string Summary
    {
        get
        {
            lock (Gate)
                return $"tokens: in {PromptTokens:N0} (cache hit {CacheHitTokens:N0}) out {CompletionTokens:N0}"
                     + $" · {Rounds} rounds · ≈{EstimatedCost:F3} {AgentConfig.PriceCurrency}";
        }
    }
}
