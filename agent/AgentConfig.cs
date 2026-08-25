using MelonLoader;

namespace IronNestAgentBridge.Agent;

/// <summary>LLM agent settings, persisted by MelonLoader in UserData\MelonPreferences.cfg.</summary>
public static class AgentConfig
{
    private static MelonPreferences_Category _category = null!;
    private static MelonPreferences_Entry<string> _apiKey = null!;
    private static MelonPreferences_Entry<string> _baseUrl = null!;
    private static MelonPreferences_Entry<string> _model = null!;
    private static MelonPreferences_Entry<int> _maxTokens = null!;
    private static MelonPreferences_Entry<bool> _autoStart = null!;
    private static MelonPreferences_Entry<bool> _llmControl = null!;
    private static MelonPreferences_Entry<bool> _priorityQueue = null!;
    private static MelonPreferences_Entry<int> _fcsQueueDepth = null!;

    public static void Initialize()
    {
        _category = MelonPreferences.CreateCategory("AgentBridge");
        _apiKey = _category.CreateEntry("ApiKey", "", description: "LLM API key (OpenAI-compatible endpoint)");
        _baseUrl = _category.CreateEntry("BaseUrl", "https://api.deepseek.com");
        _model = _category.CreateEntry("Model", "deepseek-v4-flash");
        _maxTokens = _category.CreateEntry("MaxTokens", 393216);
        _autoStart = _category.CreateEntry("AutoStart", true, description: "Start the FDO agent automatically once the scene binds");
        _llmControl = _category.CreateEntry("LlmControl", false, description: "Master switch: LLM is allowed to control fire missions (default off; F11 or panel button toggles)");
        _priorityQueue = _category.CreateEntry("PriorityQueue", false, description: "Optional: stage missions in a bridge-side queue (dispatch-time revalidation, FCS kept shallow). Default off — FCS has native priority ordering.");
        _fcsQueueDepth = _category.CreateEntry("FcsQueueDepth", 2, description: "Dispatch from the priority queue only while FCS pending tasks are below this");
        _enableHttpApi = _category.CreateEntry("EnableHttpApi", false,
            description: "Expose the local debug HTTP API (fire/draw/requisition endpoints). Keep OFF unless developing — RCE surface for local processes.");
        InitializePricing();

        // The agent is ALWAYS stopped on boot: LLM control is a per-session act (F11 /
        // panel), never resumed from a previous session's persisted value.
        _llmControl.Value = false;
    }

    public static bool LlmControl
    {
        get => _llmControl.Value;
        set { _llmControl.Value = value; MelonPreferences.Save(); }
    }

    public static bool PriorityQueue
    {
        get => _priorityQueue.Value;
        set { _priorityQueue.Value = value; MelonPreferences.Save(); }
    }

    public static int FcsQueueDepth => Math.Max(1, _fcsQueueDepth.Value);

    private static MelonPreferences_Entry<bool> _enableHttpApi = null!;

    /// <summary>
    /// Debug HTTP API (127.0.0.1:17171). Default OFF: the endpoints can fire guns, buy
    /// cards and draw on the map, so any local process — or a web page doing CSRF against
    /// localhost — could drive the game. Enable only on a dev machine.
    /// </summary>
    public static bool EnableHttpApi => _enableHttpApi.Value;

    private static MelonPreferences_Entry<double> _priceInMiss = null!;
    private static MelonPreferences_Entry<double> _priceInHit = null!;
    private static MelonPreferences_Entry<double> _priceOut = null!;
    private static MelonPreferences_Entry<string> _priceCurrency = null!;

    private static void InitializePricing()
    {
        // deepseek-v4-flash peak pricing (off-peak is half); edit in MelonPreferences.cfg.
        _priceInMiss = _category.CreateEntry("PriceInputCacheMissPer1M", 0.44, description: "Input price per 1M tokens (cache miss)");
        _priceInHit = _category.CreateEntry("PriceInputCacheHitPer1M", 0.014, description: "Input price per 1M tokens (cache hit)");
        _priceOut = _category.CreateEntry("PriceOutputPer1M", 1.32, description: "Output price per 1M tokens");
        _priceCurrency = _category.CreateEntry("PriceCurrency", "USD");
    }

    public static double PriceInputCacheMiss => _priceInMiss.Value;
    public static double PriceInputCacheHit => _priceInHit.Value;
    public static double PriceOutput => _priceOut.Value;
    public static string PriceCurrency => _priceCurrency.Value;

    public static string ApiKey => _apiKey.Value;
    public static string BaseUrl => _baseUrl.Value.TrimEnd('/');
    public static string Model => _model.Value;
    public static int MaxTokens => _maxTokens.Value;
    public static bool AutoStart => _autoStart.Value;
}
