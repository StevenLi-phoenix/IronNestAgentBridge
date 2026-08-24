using System.Text;
using System.Text.Json;

namespace IronNestAgentBridge.Agent;

/// <summary>Minimal OpenAI-compatible chat-completions client (DeepSeek by default).</summary>
public static class LlmClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(300) };

    /// <summary>
    /// Streaming chat completion (SSE). onDelta fires per content chunk from a background
    /// thread; returns the full accumulated reply.
    /// </summary>
    public static string ChatStream(string systemPrompt, string userContent, Action<string> onDelta, CancellationToken ct)
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
            stream = true,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AgentConfig.BaseUrl}/chat/completions");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {AgentConfig.ApiKey}");
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        using var response = Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            var err = response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            throw new HttpRequestException($"LLM API {(int)response.StatusCode}: {Truncate(err, 300)}");
        }

        var full = new StringBuilder();
        using var stream = response.Content.ReadAsStreamAsync(ct).GetAwaiter().GetResult();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            ct.ThrowIfCancellationRequested();
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
                continue;
            var data = line[6..];
            if (data == "[DONE]")
                break;
            try
            {
                using var doc = JsonDocument.Parse(data);
                var delta = doc.RootElement.GetProperty("choices")[0].GetProperty("delta");
                if (delta.TryGetProperty("content", out var c) && c.GetString() is { Length: > 0 } chunk)
                {
                    full.Append(chunk);
                    onDelta(chunk);
                }
            }
            catch (JsonException) { /* keep-alive or malformed frame — skip */ }
        }
        return full.ToString();
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
