using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// OpenAI-compatible HTTP client. The only I/O boundary to the LLM.
/// Handles JSON repair, fuzzy tool matching, and retry logic.
/// Streaming logic lives in ModelStreaming.cs (Rule #8: files ≤ 300 lines).
/// </summary>
public class ModelClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly double _temperature;
    private readonly List<ToolDef> _tools;
    private int _totalTokensUsed;
    private bool _disposed;

    public record ToolDef(string Name, string Description, JsonElement ParametersSchema);

    /// <summary>Total tokens consumed across all calls in this session.</summary>
    public int TotalTokensUsed => _totalTokensUsed;

    /// <summary>Total thinking/reasoning tokens consumed across all calls.</summary>
    public int TotalThinkingTokens { get; private set; } = 0;

    /// <summary>All thinking/reasoning content captured this session (for TUI).</summary>
    public List<string> ThinkingLog { get; } = new();

    /// <summary>
    /// Create a ModelClient for an OpenAI-compatible endpoint.
    /// Optionally provide an API key and extra headers for providers like OpenRouter.
    /// </summary>
    public ModelClient(string endpoint, string model, double temperature = 0.3,
        string? apiKey = null, Dictionary<string, string>? extraHeaders = null)
    {
        _endpoint = endpoint.TrimEnd('/');
        _model = model;
        _temperature = temperature;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _tools = new List<ToolDef>();
        _totalTokensUsed = 0;

        if (!string.IsNullOrEmpty(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        if (extraHeaders != null)
        {
            foreach (var (key, value) in extraHeaders)
                _http.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        }
    }

    public void Dispose() { if (!_disposed) { _http.Dispose(); _disposed = true; } }

    /// <summary>Register a tool that the model can call.</summary>
    public void RegisterTool(string name, string description, JsonElement parametersSchema) =>
        _tools.Add(new ToolDef(name, description, parametersSchema));

    /// <summary>
    /// Send a chat completion request with tool definitions.
    /// Retries on malformed responses (up to maxRetries times).
    /// Retries append hints to the last user message (not system messages).
    /// If enableStreaming is true, uses SSE and forwards deltas via the observer.
    /// If enableStreaming is false (default), uses standard request/response.
    /// </summary>
    public async Task<ModelResponse> Complete(List<ChatMessage> messages, CancellationToken ct = default,
        int maxRetries = 3, IAgentObserver? observer = null, bool enableStreaming = false)
    {
        var toolSchemas = BuildToolSchemas();

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var requestBody = BuildRequestBody(messages, toolSchemas, enableStreaming);
            JsonElement? response;

            if (enableStreaming)
            {
                response = await ModelStreaming.SendStreaming(_http, _endpoint, requestBody, observer!, ct);
                // Fallback: if streaming failed or returned empty, retry without streaming
                if (response == null && observer != null)
                {
                    observer.OnError("[Streaming failed, retrying without streaming]");
                    var nonStreamBody = BuildRequestBody(messages, toolSchemas, streaming: false);
                    response = await SendRequest(nonStreamBody, ct);
                }
            }
            else
            {
                response = await SendRequest(requestBody, ct);
            }

            if (response == null)
            {
                if (attempt < maxRetries)
                {
                    messages = AppendRetryHint(messages, "The server returned no response. Try again.");
                    continue;
                }
                return new ModelResponse("ERROR: No response from model", new List<ToolCall>(), 0);
            }

            var parsed = ParseResponse(response.Value);
            _totalTokensUsed += parsed.TokensUsed;

            if (parsed.ToolCalls.Count == 0 && string.IsNullOrWhiteSpace(parsed.Content) && attempt < maxRetries)
            {
                messages = AppendRetryHint(messages,
                    "Your previous response could not be parsed. " +
                    "Respond with valid tool calls using the correct function names and JSON arguments.");
                continue;
            }

            return parsed;
        }

        return new ModelResponse("ERROR: Max retries exceeded", new List<ToolCall>(), 0);
    }

    /// <summary>Build the OpenAI-compatible request body.</summary>
    private string BuildRequestBody(List<ChatMessage> messages, JsonElement toolSchemas, bool streaming = false)
    {
        var body = new Dictionary<string, object>
        {
            ["model"] = _model,
            ["messages"] = MessageSerializer.ToArray(messages),
            ["temperature"] = _temperature,
        };
        if (streaming)
            body["stream"] = true;
        if (_tools.Count > 0)
            body["tools"] = toolSchemas;

        return JsonSerializer.Serialize(body, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
    }

    /// <summary>Build the tools array for the API request.</summary>
    private JsonElement BuildToolSchemas()
    {
        if (_tools.Count == 0)
            return JsonSerializer.Deserialize<JsonElement>("[]");

        var tools = _tools.Select(t => new Dictionary<string, object>
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object>
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.ParametersSchema
            }
        }).ToList();

        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(tools))!;
    }

    /// <summary>
    /// Send a non-streaming HTTP request. Propagates cancellation immediately.
    /// </summary>
    private async Task<JsonElement?> SendRequest(string requestBody, CancellationToken ct)
    {
        try
        {
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync($"{_endpoint}/chat/completions", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"Model API error ({response.StatusCode}): {errorBody}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonDocument.Parse(json).RootElement;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Model request failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Parse the API response into a ModelResponse.</summary>
    private ModelResponse ParseResponse(JsonElement response)
    {
        string content = "";
        var toolCalls = new List<ToolCall>();
        int tokensUsed = 0;
        string? thinkingContent = null;
        int thinkingTokens = 0;

        if (response.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("total_tokens", out var total))
                tokensUsed = total.GetInt32();

            // OpenAI o1/o3 style: usage.completion_tokens_details.reasoning_tokens
            if (usage.TryGetProperty("completion_tokens_details", out var details) &&
                details.TryGetProperty("reasoning_tokens", out var rt))
                thinkingTokens = rt.GetInt32();
        }

        if (!response.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            return new ModelResponse("ERROR: No choices in response", toolCalls, tokensUsed);

        var message = choices[0].GetProperty("message");

        if (message.TryGetProperty("content", out var contentProp) &&
            contentProp.ValueKind != JsonValueKind.Null)
            content = contentProp.GetString() ?? "";

        // Kimi K2.5 / DeepSeek style: message.reasoning_content
        if (message.TryGetProperty("reasoning_content", out var rc) &&
            rc.ValueKind != JsonValueKind.Null)
        {
            thinkingContent = rc.GetString();
        }
        // Ollama thinking models: message.thinking
        else if (message.TryGetProperty("thinking", out var tk) &&
            tk.ValueKind != JsonValueKind.Null)
        {
            thinkingContent = tk.GetString();
        }

        if (message.TryGetProperty("tool_calls", out var tcProp) &&
            tcProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var tc in tcProp.EnumerateArray())
            {
                var id = tc.GetProperty("id").GetString() ?? Guid.NewGuid().ToString();
                var func = tc.GetProperty("function");
                var rawName = func.GetProperty("name").GetString() ?? "";
                var matchedName = ResolveToolName(rawName);
                var rawArgs = func.TryGetProperty("arguments", out var ap) ? ap.GetString() ?? "{}" : "{}";
                var parsedArgs = JsonRepair.Repair(rawArgs);
                toolCalls.Add(new ToolCall(id, matchedName, parsedArgs));
            }
        }

        // Capture thinking content for the session log
        if (!string.IsNullOrEmpty(thinkingContent))
        {
            ThinkingLog.Add(thinkingContent);
            // If API didn't report reasoning_tokens, estimate from content length
            // Models use ~1 token per 4 chars for English, ~1 token per 2 chars for code/mixed
            if (thinkingTokens == 0)
                thinkingTokens = thinkingContent.Length / 3;
        }
        TotalThinkingTokens += thinkingTokens;

        return new ModelResponse(content, toolCalls, tokensUsed, thinkingContent, thinkingTokens);
    }

    /// <summary>
    /// Fuzzy tool name matching. Normalizes to lowercase, strips underscores/hyphens.
    /// Falls back to Levenshtein distance ≤ 2.
    /// </summary>
    private string ResolveToolName(string rawName)
    {
        var normalized = rawName.ToLowerInvariant().Replace("_", "").Replace("-", "");

        foreach (var tool in _tools)
        {
            var tn = tool.Name.ToLowerInvariant().Replace("_", "").Replace("-", "");
            if (normalized == tn) return tool.Name;
        }

        foreach (var tool in _tools)
        {
            var tn = tool.Name.ToLowerInvariant().Replace("_", "").Replace("-", "");
            if (JsonRepair.LevenshteinDistance(normalized, tn) <= 2)
            {
                Console.Error.WriteLine($"WARN: Fuzzy matched tool '{rawName}' -> '{tool.Name}'");
                return tool.Name;
            }
        }

        Console.Error.WriteLine($"WARN: Unknown tool '{rawName}', passing through");
        return rawName;
    }

    /// <summary>
    /// Append a retry hint to the last user message.
    /// Preserves the single-system-message principle (Rule #1).
    /// </summary>
    private static List<ChatMessage> AppendRetryHint(List<ChatMessage> messages, string hint)
    {
        var copy = new List<ChatMessage>(messages);
        for (int i = copy.Count - 1; i >= 0; i--)
        {
            if (copy[i].Role == "user" && copy[i].Content != null)
            {
                copy[i] = ChatMessage.User(copy[i].Content + "\n\n[" + hint + "]");
                return copy;
            }
        }
        copy.Add(ChatMessage.User(hint));
        return copy;
    }
}