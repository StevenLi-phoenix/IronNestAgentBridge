using System.Text;
using System.Text.Json;

namespace IronNestAgentBridge.Agent;

/// <summary>
/// The whole pipe to the LLM provider: an OpenAI-compatible streaming chat completion, the
/// multi-round function-calling loop, and the forced wrap-up when the tool budget runs out.
///
/// Threading: <see cref="ChatStream"/> blocks synchronously on HTTP and on stream reads, so it
/// must only ever run on the agent's background thread. Calling it from Unity's main thread
/// hangs the game.
///
/// Prefix-cache contract: the message list belongs to the caller. This class only appends
/// assistant / tool / wrap-up turns at the tail and never rewrites, trims, reorders or edits an
/// existing element, which keeps the history byte-stable and hits the provider's context cache.
/// Reasoning tokens and the "🔧" tool receipts are display-only and never enter the history.
/// </summary>
public static class LlmClient
{
    /// <summary>Hardcoded on purpose: a fire-direction officer must not be creative.</summary>
    private const double Temperature = 0.3;

    /// <summary>Wrap-up text returned when the model produces nothing after the tool budget.</summary>
    private const string ToolLimitPlaceholder = "(tool round limit reached)";

    /// <summary>
    /// Verbatim (系统) message that ends a decision whose tool budget is exhausted. The mixed
    /// half-width comma / Chinese punctuation and the space after the colon are intentional.
    /// </summary>
    private const string ToolLimitWrapUpPrompt =
        "(系统) 本轮工具调用次数已达上限。停止调用工具, 立即用纯文本总结: 已完成的动作、未完成的意图(下轮优先做什么)。";

    /// <summary>
    /// Process-wide singleton. Required, not an optimisation: a client per round would exhaust
    /// sockets and throw away connection reuse, which in turn breaks the provider-side prefix
    /// cache affinity. The 300 s timeout is hardcoded — a single decision with many tool rounds
    /// legitimately streams for minutes.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(300) };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>One assembling tool call; deltas arrive fragmented across frames.</summary>
    private sealed class ToolCall
    {
        public string Id = "";
        public string Name = "";
        public string Arguments = "";
    }

    /// <summary>
    /// Runs one decision to completion: streams rounds, executes tool calls, and returns the
    /// final assistant plain text. Assistant / tool turns are appended to
    /// <paramref name="messages"/> in place.
    /// </summary>
    /// <param name="messages">Caller-owned history. The same instance must be passed for every
    /// decision of a session, otherwise the prefix cache misses.</param>
    /// <param name="toolsJson">JSON text of the OpenAI <c>tools</c> array; <c>null</c> disables
    /// tools for this call at the protocol level.</param>
    /// <param name="toolExecutor">Called synchronously on this thread; implementations that need
    /// game objects must marshal to the main thread themselves.</param>
    /// <param name="onDelta">Receives answer fragments, reasoning fragments, the
    /// 〔思考〕/〔回答〕 markers and the 🔧 tool receipts. Called on the streaming thread, so it
    /// must not block.</param>
    /// <param name="ct">Stop / F9 reset token. Cancellation propagates out of this method.</param>
    public static string ChatStream(
        List<object> messages,
        string? toolsJson,
        Func<string, JsonElement, string>? toolExecutor,
        Action<string> onDelta,
        CancellationToken ct)
    {
        // Configurable budget (default 64). Clamped: a zero or negative setting would skip
        // straight to the wrap-up and make the agent useless.
        var maxToolRounds = Math.Max(1, AgentConfig.MaxToolRounds);

        for (var round = 0; round < maxToolRounds; round++)
        {
            var (text, toolCalls) = StreamOneRound(messages, toolsJson, onDelta, ct);

            if (toolCalls.Count == 0 || toolExecutor == null)
            {
                messages.Add(new Dictionary<string, object?>
                {
                    ["role"] = "assistant",
                    ["content"] = text,
                });
                return text;
            }

            // Everything appended from here on belongs to one tool round: the assistant message
            // carrying the tool_calls plus one tool message per call.
            var roundStart = messages.Count;

            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                // Must be null, never "": some providers reject an empty string next to tool_calls.
                ["content"] = string.IsNullOrEmpty(text) ? null : text,
                ["tool_calls"] = toolCalls.Select(call => new Dictionary<string, object?>
                {
                    ["id"] = call.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = call.Name,
                        // Raw argument text, passed through unparsed and unformatted.
                        ["arguments"] = call.Arguments,
                    },
                }).ToList<object>(),
            });

            try
            {
                ExecuteToolCalls(messages, toolCalls, toolExecutor, onDelta, ct);
            }
            catch (OperationCanceledException)
            {
                // The only place the append-only rule is broken, and it has to be: a stop mid
                // round leaves an assistant tool_calls message whose calls were never all
                // answered, which the provider rejects on the next request. Drop the whole
                // unpaired round so the history stays valid.
                messages.RemoveRange(roundStart, messages.Count - roundStart);
                throw;
            }
        }

        // Budget exhausted. Never return a placeholder on its own: the history has to close on
        // an assistant turn (not on a pile of tool results), and the next decision needs the
        // model's own summary of what it did and what it left undone.
        messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = ToolLimitWrapUpPrompt,
        });

        // Tools are withheld here, so the model cannot keep calling them.
        var (finalText, _) = StreamOneRound(messages, null, onDelta, ct);

        // The (系统) turn and this reply stay in the history: rewriting them would shift the
        // prefix and cost a full cache miss on every later round.
        messages.Add(new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = finalText,
        });

        return string.IsNullOrEmpty(finalText) ? ToolLimitPlaceholder : finalText;
    }

    /// <summary>
    /// Executes the round's tool calls in order, appending one <c>tool</c> message each.
    /// </summary>
    private static void ExecuteToolCalls(
        List<object> messages,
        List<ToolCall> toolCalls,
        Func<string, JsonElement, string> toolExecutor,
        Action<string> onDelta,
        CancellationToken ct)
    {
        foreach (var call in toolCalls)
        {
            // Invariant: after a stop or an F9 reset, no tool may run any more. A leftover call
            // would act on a world view that no longer exists — and really fire a gun.
            ct.ThrowIfCancellationRequested();

            string result;
            try
            {
                var argumentsText = string.IsNullOrEmpty(call.Arguments) ? "{}" : call.Arguments;

                // Cloned because the document is released on leaving the using scope; without
                // the clone the executor would hold a dangling element.
                JsonElement args;
                using (var document = JsonDocument.Parse(argumentsText))
                {
                    args = document.RootElement.Clone();
                }

                result = toolExecutor(call.Name, args);
            }
            catch (Exception ex)
            {
                // One failing tool must never abort the decision; the model gets the failure as
                // a normal result and can react to it.
                result = JsonSerializer.Serialize(new { error = $"tool failed: {ex.Message}" });
            }

            // Display only — this line never enters the history.
            onDelta($"\n🔧 {call.Name}({call.Arguments}) → {result}\n");

            messages.Add(new Dictionary<string, object?>
            {
                ["role"] = "tool",
                ["tool_call_id"] = call.Id,
                ["content"] = result,
            });
        }
    }

    /// <summary>
    /// One streaming HTTP request. Returns the round's plain text plus the tool calls assembled
    /// from the deltas, and reports usage exactly once for the request.
    /// </summary>
    private static (string Text, List<ToolCall> ToolCalls) StreamOneRound(
        List<object> messages,
        string? toolsJson,
        Action<string> onDelta,
        CancellationToken ct)
    {
        using var request = BuildRequest(messages, toolsJson);
        using var response = Http
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
        {
            var body = response.Content.ReadAsStringAsync(ct).GetAwaiter().GetResult() ?? "";
            if (body.Length > 300) body = body[..300];
            throw new HttpRequestException($"LLM API {(int)response.StatusCode}: {body}");
        }

        using var stream = response.Content.ReadAsStreamAsync(ct).GetAwaiter().GetResult();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var text = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        var reasoning = false;

        // Usage is metered once per HTTP request; a provider may emit several usage frames, and
        // the last one is the authoritative total.
        var haveUsage = false;
        long promptTokens = 0, completionTokens = 0, cacheHitTokens = 0, cacheMissTokens = 0;

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();

            // Everything that is not a data frame — event:, id:, blank lines, comments — is noise.
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;

            var payload = line[6..];
            if (payload == "[DONE]") break;

            JsonDocument frame;
            try
            {
                frame = JsonDocument.Parse(payload);
            }
            catch (JsonException)
            {
                // Provider keep-alive / heartbeat chatter, not an error. Only JsonException is
                // swallowed here; anything else must surface.
                continue;
            }

            using (frame)
            {
                var root = frame.RootElement;

                // Usage must be read before the choices check: usage frames normally carry an
                // empty choices array.
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    haveUsage = true;
                    promptTokens = ReadLong(usage, "prompt_tokens");
                    completionTokens = ReadLong(usage, "completion_tokens");
                    (cacheHitTokens, cacheMissTokens) = ReadCacheSplit(usage, promptTokens);
                }

                if (!root.TryGetProperty("choices", out var choices)
                    || choices.ValueKind != JsonValueKind.Array
                    || choices.GetArrayLength() == 0)
                {
                    continue;
                }

                if (!choices[0].TryGetProperty("delta", out var delta)
                    || delta.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // Reasoning tokens are shown and then thrown away. They must never reach the
                // history: the provider does not echo them back, so keeping them would make the
                // prefix jitter and destroy the cache.
                var thought = ReadString(delta, "reasoning_content");
                if (!string.IsNullOrEmpty(thought))
                {
                    if (!reasoning)
                    {
                        reasoning = true;
                        onDelta("〔思考〕");
                    }
                    onDelta(thought);
                }

                var content = ReadString(delta, "content");
                if (!string.IsNullOrEmpty(content))
                {
                    if (reasoning)
                    {
                        reasoning = false;
                        onDelta("\n〔回答〕");
                    }
                    text.Append(content);
                    onDelta(content);
                }

                if (delta.TryGetProperty("tool_calls", out var deltaCalls)
                    && deltaCalls.ValueKind == JsonValueKind.Array)
                {
                    foreach (var deltaCall in deltaCalls.EnumerateArray())
                    {
                        AccumulateToolCall(toolCalls, deltaCall);
                    }
                }
            }
        }

        if (haveUsage)
        {
            UsageMeter.AddRound(promptTokens, completionTokens, cacheHitTokens, cacheMissTokens);
        }

        // Slots created only to pad an out-of-order index stay nameless; drop them.
        var assembled = toolCalls.Where(call => !string.IsNullOrEmpty(call.Name)).ToList();
        return (text.ToString(), assembled);
    }

    private static HttpRequestMessage BuildRequest(List<object> messages, string? toolsJson)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = AgentConfig.Model,
            ["messages"] = messages,
            // Output cap, sent unchanged every round. 393216 is DeepSeek's 384k ceiling and is
            // unrelated to the 400k prompt-token auto-compact threshold in the agent module.
            ["max_tokens"] = AgentConfig.MaxTokens,
            ["temperature"] = Temperature,
            ["stream"] = true,
            ["stream_options"] = new Dictionary<string, object?> { ["include_usage"] = true },
        };

        if (toolsJson != null)
        {
            // Embedded as parsed JSON, not as an escaped string. When there are no tools the
            // field is absent entirely — neither null nor [].
            using var document = JsonDocument.Parse(toolsJson);
            body["tools"] = document.RootElement.Clone();
        }

        var request = new HttpRequestMessage(HttpMethod.Post, AgentConfig.BaseUrl + "/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Utf8NoBom, "application/json"),
        };

        // Unvalidated on purpose: keys containing characters the header parser rejects would
        // otherwise throw instead of being sent.
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + AgentConfig.ApiKey);
        return request;
    }

    /// <summary>
    /// Splits the prompt tokens into cache hit / miss. DeepSeek's private fields win; the
    /// OpenAI-standard <c>prompt_tokens_details.cached_tokens</c> is the fallback so other
    /// endpoints still get cache-aware pricing.
    /// </summary>
    private static (long Hit, long Miss) ReadCacheSplit(JsonElement usage, long promptTokens)
    {
        if (usage.TryGetProperty("prompt_cache_hit_tokens", out _)
            || usage.TryGetProperty("prompt_cache_miss_tokens", out _))
        {
            return (ReadLong(usage, "prompt_cache_hit_tokens"), ReadLong(usage, "prompt_cache_miss_tokens"));
        }

        if (usage.TryGetProperty("prompt_tokens_details", out var details)
            && details.ValueKind == JsonValueKind.Object
            && details.TryGetProperty("cached_tokens", out _))
        {
            var hit = ReadLong(details, "cached_tokens");
            return (hit, Math.Max(0, promptTokens - hit));
        }

        // Nothing reported: the meter then charges the whole prompt at the miss rate.
        return (0, 0);
    }

    /// <summary>
    /// Merges one tool-call delta into its slot. Names and arguments arrive in fragments and are
    /// concatenated; ids are overwritten. Providers may skip or reorder indices, so missing
    /// slots are padded with empty placeholders.
    /// </summary>
    private static void AccumulateToolCall(List<ToolCall> toolCalls, JsonElement deltaCall)
    {
        var index = (int)ReadLong(deltaCall, "index");
        if (index < 0) return;

        while (toolCalls.Count <= index) toolCalls.Add(new ToolCall());
        var slot = toolCalls[index];

        var id = ReadString(deltaCall, "id");
        if (!string.IsNullOrEmpty(id)) slot.Id = id;

        if (!deltaCall.TryGetProperty("function", out var function)
            || function.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var name = ReadString(function, "name");
        if (!string.IsNullOrEmpty(name)) slot.Name += name;

        var arguments = ReadString(function, "arguments");
        if (!string.IsNullOrEmpty(arguments)) slot.Arguments += arguments;
    }

    /// <summary>Missing, null or non-numeric counts as 0.</summary>
    private static long ReadLong(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
           && value.TryGetInt64(out var number)
            ? number
            : 0;

    private static string? ReadString(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
