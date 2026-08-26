using MelonLoader;

namespace IronNestAgentBridge.Agent;

/// <summary>
/// The <c>[AgentBridge]</c> section of <c>UserData\MelonPreferences.cfg</c>.
///
/// <see cref="Initialize"/> must run before anything reads a value and before either the HTTP
/// server or the agent starts.
///
/// Runtime trap: the file must never be hand-edited while the game runs. Any
/// <c>MelonPreferences.Save()</c> rewrites the whole file from memory, so manual edits are
/// erased. Toggle switches through F11 / the panel instead.
/// </summary>
public static class AgentConfig
{
    private static MelonPreferences_Category _category = null!;
    private static MelonPreferences_Entry<string> _apiKey = null!;
    private static MelonPreferences_Entry<string> _baseUrl = null!;
    private static MelonPreferences_Entry<string> _model = null!;
    private static MelonPreferences_Entry<int> _maxTokens = null!;
    private static MelonPreferences_Entry<int> _maxToolRounds = null!;
    private static MelonPreferences_Entry<bool> _llmControl = null!;
    private static MelonPreferences_Entry<bool> _enableHttpApi = null!;
    private static MelonPreferences_Entry<double> _priceInputCacheMiss = null!;
    private static MelonPreferences_Entry<double> _priceInputCacheHit = null!;
    private static MelonPreferences_Entry<double> _priceOutput = null!;
    private static MelonPreferences_Entry<string> _priceCurrency = null!;

    public static void Initialize()
    {
        _category = MelonPreferences.CreateCategory("AgentBridge");

        _apiKey = _category.CreateEntry("ApiKey", "",
            description: "LLM API key (OpenAI-compatible endpoint)");
        _baseUrl = _category.CreateEntry("BaseUrl", "https://api.deepseek.com");
        _model = _category.CreateEntry("Model", "deepseek-v4-flash");

        // 393216 = DeepSeek's 384k max output, the ceiling for a 1M-context model. Sent with
        // every round; it is the OUTPUT cap and is unrelated to the 400k prompt-token threshold
        // that triggers auto-compact.
        _maxTokens = _category.CreateEntry("MaxTokens", 393216);

        _maxToolRounds = _category.CreateEntry("MaxToolRounds", 64,
            description: "Tool-call rounds allowed per decision before the forced text wrap-up");

        _llmControl = _category.CreateEntry("LlmControl", false,
            description: "Master switch: LLM is allowed to control fire missions (default off; F11 or panel button toggles)");
        _enableHttpApi = _category.CreateEntry("EnableHttpApi", false,
            description: "Expose the local debug HTTP API (fire/draw/requisition endpoints). Keep OFF unless developing — RCE surface for local processes.");

        // Peak-hour list price for deepseek-v4-flash. Off-peak halving is applied at metering
        // time, so there is no second price table here.
        _priceInputCacheMiss = _category.CreateEntry("PriceInputCacheMissPer1M", 0.44,
            description: "Input price per 1M tokens (cache miss)");
        _priceInputCacheHit = _category.CreateEntry("PriceInputCacheHitPer1M", 0.014,
            description: "Input price per 1M tokens (cache hit)");
        _priceOutput = _category.CreateEntry("PriceOutputPer1M", 1.32,
            description: "Output price per 1M tokens");
        _priceCurrency = _category.CreateEntry("PriceCurrency", "USD");

        // Fire control is granted by hand once per session. Never inherit it from the last one:
        // force the switch off and flush immediately so a crash cannot resurrect a stale "on".
        _llmControl.Value = false;
        MelonPreferences.Save();
    }

    public static string ApiKey => _apiKey.Value;

    /// <summary>Trailing slash removed, so "https://host/" and "https://host" behave alike.</summary>
    public static string BaseUrl => (_baseUrl.Value ?? "").TrimEnd('/');

    public static string Model => _model.Value;
    public static int MaxTokens => _maxTokens.Value;

    /// <summary>Rounds of tool calls per decision; on exhaustion the client forces a summary.</summary>
    public static int MaxToolRounds => _maxToolRounds.Value;

    /// <summary>Writable; every write is flushed at once so hotkey toggles survive a crash.</summary>
    public static bool LlmControl
    {
        get => _llmControl.Value;
        set
        {
            _llmControl.Value = value;
            MelonPreferences.Save();
        }
    }

    public static bool EnableHttpApi => _enableHttpApi.Value;
    public static double PriceInputCacheMiss => _priceInputCacheMiss.Value;
    public static double PriceInputCacheHit => _priceInputCacheHit.Value;
    public static double PriceOutput => _priceOutput.Value;
    public static string PriceCurrency => _priceCurrency.Value;
}
