using System.Text;
using System.Text.Json;

namespace IronNestAgentBridge.Agent;

/// <summary>Minimal OpenAI-compatible chat-completions client (DeepSeek by default).</summary>
public static class LlmClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(300) };

    public static string Chat(string systemPrompt, string userContent, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = AgentConfig.Model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent },
            },
            max_tokens = AgentConfig.MaxTokens,
            temperature = 0.3,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AgentConfig.BaseUrl}/chat/completions");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {AgentConfig.ApiKey}");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = Http.SendAsync(request, ct).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"LLM API {(int)response.StatusCode}: {Truncate(body, 300)}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
