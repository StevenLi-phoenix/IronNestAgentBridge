using System.Text;
using System.Text.Json;

namespace IronNestAgentBridge.Agent;

/// <summary>
/// OpenAI-compatible chat client with SSE streaming and function calling.
/// Runs a multi-round loop: stream → execute requested tools → feed results back →
/// repeat until the model produces a final text answer.
/// </summary>
public static class LlmClient
{
    private const int MaxToolRounds = 64;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(300) };

    /// <summary>
    /// Continue a persistent conversation. The caller owns `messages` (system prompt + all
    /// prior turns + the newest user message); this appends assistant/tool turns in place so
    /// the whole history stays a byte-stable prefix for the provider's context cache.
    /// </summary>
    public static string ChatStream(
        List<object> messages,
        string? toolsJson,
        Func<string, JsonElement, string>? toolExecutor,
        Action<string> onDelta,
        CancellationToken ct)
    {
        for (var round = 0; round < MaxToolRounds; round++)
        {
            var (content, toolCalls) = StreamOneRound(messages, toolsJson, onDelta, ct);

            if (toolCalls.Count == 0 || toolExecutor == null)
            {
                messages.Add(new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = content });
                return content;
            }

            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["content"] = content.Length > 0 ? content : null,
                ["tool_calls"] = toolCalls.Select(tc => new Dictionary<string, object?>
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?> { ["name"] = tc.Name, ["arguments"] = tc.Arguments },
                }).ToList(),
            });

            foreach (var tc in toolCalls)
            {
                // A reset/stop between rounds must not execute stale-worldview tools.
                ct.ThrowIfCancellationRequested();
                string result;
                try
                {
                    using var argsDoc = JsonDocument.Parse(tc.Arguments.Length > 0 ? tc.Arguments : "{}");
                    result = toolExecutor(tc.Name, argsDoc.RootElement.Clone());
                }
                catch (Exception ex)
                {
                    result = JsonSerializer.Serialize(new { error = $"tool failed: {ex.Message}" });
                }
                onDelta($"\n🔧 {tc.Name}({tc.Arguments}) → {result}\n");
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = tc.Id,
                    ["content"] = result,
                });
            }
        }

        // Cap reached: force one text-only pass so the model still delivers a proper
        // decision summary and the history closes on an assistant turn (instead of
        // dangling tool results plus a placeholder).
        messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = "(系统) 本轮工具调用次数已达上限。停止调用工具, 立即用纯文本总结: 已完成的动作、未完成的意图(下轮优先做什么)。",
        });
        var (finalText, _) = StreamOneRound(messages, null, onDelta, ct);
        messages.Add(new Dictionary<string, object?> { ["role"] = "assistant", ["content"] = finalText });
        return finalText.Length > 0 ? finalText : "(tool round limit reached)";
    }

    private sealed class ToolCall
    {
        public string Id = "";
        public string Name = "";
        public string Arguments = "";
    }

    private static (string content, List<ToolCall> toolCalls) StreamOneRound(
        List<object> messages, string? toolsJson, Action<string> onDelta, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = AgentConfig.Model,
            ["messages"] = messages,
            ["max_tokens"] = AgentConfig.MaxTokens,
            ["temperature"] = 0.3,
            ["stream"] = true,
            ["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true },
        };
        if (toolsJson != null)
        {
            using var toolsDoc = JsonDocument.Parse(toolsJson);
            body["tools"] = toolsDoc.RootElement.Clone();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{AgentConfig.BaseUrl}/chat/completions");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {AgentConfig.ApiKey}");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var response = Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
        {
            var err = response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            throw new HttpRequestException($"LLM API {(int)response.StatusCode}: {(err.Length > 300 ? err[..300] : err)}");
        }

        var content = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        var inThinking = false;

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

                // Final chunk (empty choices) carries usage when stream_options.include_usage is set.
                if (doc.RootElement.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    long U(string name) => usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                    UsageMeter.AddRound(U("prompt_tokens"), U("completion_tokens"),
                        U("prompt_cache_hit_tokens"), U("prompt_cache_miss_tokens"));
                }

                if (!doc.RootElement.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    continue;
                var choice = choices[0];
                if (!choice.TryGetProperty("delta", out var delta))
                    continue;

                // Thinking tokens (DeepSeek reasoning mode): streamed to the display only —
                // never part of the final reply or the conversation history.
                if (delta.TryGetProperty("reasoning_content", out var rc) && rc.GetString() is { Length: > 0 } think)
                {
                    if (!inThinking)
                    {
                        inThinking = true;
                        onDelta("〔思考〕");
                    }
                    onDelta(think);
                }

                if (delta.TryGetProperty("content", out var c) && c.GetString() is { Length: > 0 } chunk)
                {
                    if (inThinking)
                    {
                        inThinking = false;
                        onDelta("\n〔回答〕");
                    }
                    content.Append(chunk);
                    onDelta(chunk);
                }

                if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    foreach (var call in calls.EnumerateArray())
                    {
                        var index = call.TryGetProperty("index", out var i) ? i.GetInt32() : 0;
                        while (toolCalls.Count <= index)
                            toolCalls.Add(new ToolCall());
                        var tc = toolCalls[index];
                        if (call.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } idStr)
                            tc.Id = idStr;
                        if (call.TryGetProperty("function", out var fn))
                        {
                            if (fn.TryGetProperty("name", out var n) && n.GetString() is { Length: > 0 } name)
                                tc.Name += name;
                            if (fn.TryGetProperty("arguments", out var a) && a.GetString() is { Length: > 0 } frag)
                                tc.Arguments += frag;
                        }
                    }
            }
            catch (JsonException) { /* keep-alive frame */ }
        }

        return (content.ToString(), toolCalls.Where(tc => tc.Name.Length > 0).ToList());
    }
}
