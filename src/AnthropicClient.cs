using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// Anthropic Messages API client (claude.ai / Anthropic direct).
/// Handles the full Anthropic protocol: content blocks, tool_use, tool_result,
/// extended thinking, and streaming events.
/// </summary>
public class AnthropicClient : IModelClient
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly double _temperature;
    private readonly List<ToolDef> _tools = new();
    private int _totalTokensUsed;
    private bool _disposed;

    public record ToolDef(string Name, string Description, JsonElement ParametersSchema);

    public int TotalTokensUsed => _totalTokensUsed;
    public int TotalThinkingTokens { get; private set; } = 0;
    public List<string> ThinkingLog { get; } = new();

    public AnthropicClient(string endpoint, string model, double temperature = 0.3,
        string? apiKey = null, Dictionary<string, string>? extraHeaders = null)
    {
        _endpoint = endpoint.TrimEnd('/');
        _model = model;
        _temperature = temperature;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        // Anthropic uses x-api-key header, not Authorization: Bearer
        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", apiKey);

        // Required Anthropic version header
        _http.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");

        if (extraHeaders != null)
        {
            foreach (var (key, value) in extraHeaders)
                _http.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }
    }

    public void Dispose() { if (!_disposed) { _http.Dispose(); _disposed = true; } }

    public void RegisterTool(string name, string description, JsonElement parametersSchema)
        => _tools.Add(new ToolDef(name, description, parametersSchema));

    /// <summary>
    /// Known Anthropic model context windows. Anthropic has no API to query this.
    /// Falls back to config value if model is not in this table.
    /// Updated: April 2026 — Claude 4.6 Opus/Sonnet are 1M, Haiku 4.5 is 200K.
    /// </summary>
    private static readonly Dictionary<string, int> KnownContextWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        // Current generation (April 2026)
        ["claude-opus-4-6"] = 1_000_000,
        ["claude-sonnet-4-6"] = 1_000_000,
        ["claude-haiku-4-5"] = 200_000,
        ["claude-haiku-4-5-20251001"] = 200_000,

        // Legacy (still functional, may be deprecated)
        ["claude-sonnet-4-20250514"] = 200_000,
        ["claude-opus-4-20250116"] = 200_000,
        ["claude-3-5-sonnet-20241022"] = 200_000,
        ["claude-3-5-sonnet-20240620"] = 200_000,
        ["claude-3-5-haiku-20241022"] = 200_000,
    };

    /// <summary>
    /// Return the known context window for this Anthropic model.
    /// Returns null if model is not in the known table.
    /// </summary>
    public Task<int?> QueryContextWindow(CancellationToken ct = default)
    {
        // Exact match
        if (KnownContextWindows.TryGetValue(_model, out var exact))
            return Task.FromResult<int?>(exact);

        // Prefix match (e.g. "claude-sonnet-4-20250514" matches "claude-sonnet-4")
        foreach (var (prefix, ctx) in KnownContextWindows)
        {
            if (_model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<int?>(ctx);
        }

        // Default for any unknown Anthropic model
        return Task.FromResult<int?>(200000);
    }

    public async Task<ModelResponse> Complete(List<ChatMessage> messages, CancellationToken ct = default,
        int maxRetries = 3, IAgentObserver? observer = null, bool enableStreaming = false)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Extract system message (Anthropic puts it in a top-level field)
            var systemPrompt = ExtractSystemPrompt(messages);
            var apiMessages = ConvertMessages(messages);
            var toolSchemas = BuildToolSchemas();
            var requestBody = BuildRequestBody(apiMessages, systemPrompt, toolSchemas, enableStreaming);

            JsonElement? response;
            if (enableStreaming && observer != null)
            {
                response = await AnthropicStreaming.SendStreaming(_http, _endpoint, requestBody, observer, ct);
                if (response == null && observer != null)
                {
                    observer.OnError("[Streaming failed, retrying without streaming]");
                    var nonStreamBody = BuildRequestBody(apiMessages, systemPrompt, toolSchemas, streaming: false);
                    response = await SendRequest(nonStreamBody, ct);
                }
            }
            else
                response = await SendRequest(requestBody, ct);

            if (response == null)
            {
                if (attempt < maxRetries) continue;
                return new ModelResponse("ERROR: No response from model", new List<ToolCall>(), 0);
            }

            var parsed = ParseResponse(response.Value);
            _totalTokensUsed += parsed.TokensUsed;

            if (parsed.ToolCalls.Count == 0 && string.IsNullOrWhiteSpace(parsed.Content) && attempt < maxRetries)
                continue;

            return parsed;
        }
        return new ModelResponse("ERROR: Max retries exceeded", new List<ToolCall>(), 0);
    }

    // --- Message conversion (ChatMessage → Anthropic format) ---

    /// <summary>Extract system messages into a single string for the top-level system field.</summary>
    internal static string? ExtractSystemPrompt(List<ChatMessage> messages)
    {
        var systemMsgs = messages.Where(m => m.Role == "system").Select(m => m.Content).ToList();
        return systemMsgs.Count > 0 ? string.Join("\n\n", systemMsgs) : null;
    }

    /// <summary>Convert our ChatMessage list to Anthropic messages format.</summary>
    internal static List<Dictionary<string, object>> ConvertMessages(List<ChatMessage> messages)
    {
        var result = new List<Dictionary<string, object>>();

        foreach (var msg in messages)
        {
            if (msg.Role == "system") continue; // Handled separately

            if (msg.Role == "tool")
            {
                // Anthropic: tool results go in a user message as tool_result content blocks
                var contentBlocks = new List<Dictionary<string, object>>
                {
                    new()
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = msg.ToolCallId ?? "",
                        ["content"] = msg.Content ?? ""
                    }
                };
                result.Add(new() { ["role"] = "user", ["content"] = contentBlocks });
            }
            else if (msg.Role == "assistant" && msg.ToolCalls?.Count > 0)
            {
                // Assistant with tool calls → content blocks array
                var blocks = new List<Dictionary<string, object>>();
                if (!string.IsNullOrEmpty(msg.Content))
                    blocks.Add(new() { ["type"] = "text", ["text"] = msg.Content });
                if (msg.ReasoningContent != null)
                    blocks.Add(new() { ["type"] = "thinking", ["thinking"] = msg.ReasoningContent });
                foreach (var tc in msg.ToolCalls)
                {
                    blocks.Add(new()
                    {
                        ["type"] = "tool_use",
                        ["id"] = tc.Id,
                        ["name"] = tc.Name,
                        ["input"] = JsonSerializer.Deserialize<JsonElement>(tc.Arguments.GetRawText())
                    });
                }
                result.Add(new() { ["role"] = "assistant", ["content"] = blocks });
            }
            else if (msg.Role == "assistant" && msg.ReasoningContent != null)
            {
                // Assistant with thinking but no tool calls
                var blocks = new List<Dictionary<string, object>>();
                blocks.Add(new() { ["type"] = "thinking", ["thinking"] = msg.ReasoningContent });
                if (!string.IsNullOrEmpty(msg.Content))
                    blocks.Add(new() { ["type"] = "text", ["text"] = msg.Content });
                result.Add(new() { ["role"] = "assistant", ["content"] = blocks });
            }
            else if (msg.Role == "assistant")
            {
                result.Add(new() { ["role"] = "assistant", ["content"] = msg.Content ?? "" });
            }
            else
            {
                result.Add(new() { ["role"] = msg.Role, ["content"] = msg.Content ?? "" });
            }
        }

        return result;
    }

    // --- Request/Response ---

    private string BuildRequestBody(List<Dictionary<string, object>> messages, string? systemPrompt,
        JsonElement toolSchemas, bool streaming)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["max_tokens"] = 8192,
            ["messages"] = messages,
            ["temperature"] = _temperature,
        };
        if (systemPrompt != null)
            body["system"] = systemPrompt;
        if (_tools.Count > 0)
            body["tools"] = toolSchemas;
        if (streaming)
            body["stream"] = true;

        return JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    private JsonElement BuildToolSchemas()
    {
        if (_tools.Count == 0)
            return JsonSerializer.Deserialize<JsonElement>("[]");

        // Anthropic uses "input_schema" instead of "parameters" and doesn't wrap in function/type
        var tools = _tools.Select(t => new Dictionary<string, object>
        {
            ["name"] = t.Name,
            ["description"] = t.Description,
            ["input_schema"] = t.ParametersSchema
        }).ToList();

        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(tools))!;
    }

    private async Task<JsonElement?> SendRequest(string requestBody, CancellationToken ct)
    {
        try
        {
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"{_endpoint}/v1/messages", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"Anthropic API error ({response.StatusCode}): {errorBody}");
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(json).RootElement;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Anthropic request failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Parse Anthropic Messages API response into our ModelResponse.</summary>
    private ModelResponse ParseResponse(JsonElement response)
    {
        string content = "";
        var toolCalls = new List<ToolCall>();
        int tokensUsed = 0;
        string? thinkingContent = null;
        int thinkingTokens = 0;

        // Usage: input_tokens + output_tokens
        if (response.TryGetProperty("usage", out var usage))
        {
            var input = usage.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
            var output = usage.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
            tokensUsed = input + output;
        }

        // Content blocks
        if (response.TryGetProperty("content", out var contentBlocks) &&
            contentBlocks.ValueKind == JsonValueKind.Array)
        {
            var textParts = new List<string>();
            foreach (var block in contentBlocks.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var t) ? t.GetString() : "";
                switch (type)
                {
                    case "text":
                        if (block.TryGetProperty("text", out var txt) && txt.ValueKind != JsonValueKind.Null)
                            textParts.Add(txt.GetString() ?? "");
                        break;
                    case "thinking":
                        if (block.TryGetProperty("thinking", out var th) && th.ValueKind != JsonValueKind.Null)
                        {
                            thinkingContent = th.GetString();
                            ThinkingLog.Add(thinkingContent!);
                            if (thinkingTokens == 0)
                                thinkingTokens = (thinkingContent?.Length ?? 0) / 3;
                        }
                        break;
                    case "tool_use":
                        var id = block.TryGetProperty("id", out var idi) ? idi.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                        var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var input = block.TryGetProperty("input", out var inp) ? inp : JsonSerializer.Deserialize<JsonElement>("{}");
                        var rawArgs = input.GetRawText();
                        var repairedArgs = JsonRepair.Repair(rawArgs);
                        toolCalls.Add(new ToolCall(id, name, repairedArgs));
                        break;
                }
            }
            content = string.Join("\n", textParts);
        }

        TotalThinkingTokens += thinkingTokens;
        return new ModelResponse(content, toolCalls, tokensUsed, thinkingContent, thinkingTokens);
    }
}