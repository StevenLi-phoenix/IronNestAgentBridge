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

    public static void Initialize()
    {
        _category = MelonPreferences.CreateCategory("AgentBridge");
        _apiKey = _category.CreateEntry("ApiKey", "", description: "LLM API key (OpenAI-compatible endpoint)");
        _baseUrl = _category.CreateEntry("BaseUrl", "https://api.deepseek.com");
        _model = _category.CreateEntry("Model", "deepseek-v4-flash");
        _maxTokens = _category.CreateEntry("MaxTokens", 393216);
        _autoStart = _category.CreateEntry("AutoStart", true, description: "Start the FDO agent automatically once the scene binds");
    }

    public static string ApiKey => _apiKey.Value;
    public static string BaseUrl => _baseUrl.Value.TrimEnd('/');
    public static string Model => _model.Value;
    public static int MaxTokens => _maxTokens.Value;
    public static bool AutoStart => _autoStart.Value;
}
