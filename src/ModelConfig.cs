using System.Text.Json;
using System.Text.Json.Serialization;

namespace LittleHelper;

/// <summary>
/// Model configuration loaded from ~/.little_helper/models.json.
/// Follows the pi/agent/models.json format: providers with baseUrl,
/// optional apiKey, and a list of models with id + contextWindow.
/// </summary>
class ModelConfig
{
    /// <summary>Map of provider name -> provider config.</summary>
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new();

    /// <summary>Default model id to use when --model is not specified.</summary>
    public string DefaultModel { get; set; } = "qwen3:14b";

    /// <summary>
    /// Resolve a model reference to (baseUrl, modelName, apiKey, contextWindow, temperature).
    /// Supports: "model-id" (searches all providers), "provider/model-id" (specific provider).
    /// Returns null if model not found in config.
    /// </summary>
    public ResolvedModel? Resolve(string modelRef)
    {
        // Check for "provider/model-id" syntax
        var slashIdx = modelRef.IndexOf('/');
        if (slashIdx > 0)
        {
            var providerName = modelRef[..slashIdx];
            var modelId = modelRef[(slashIdx + 1)..];

            if (Providers.TryGetValue(providerName, out var provider))
            {
                var model = provider.Models.Find(m => m.Id == modelId);
                if (model != null)
                {
                    return new ResolvedModel(
                        provider.BaseUrl, modelId, provider.ApiKey ?? "",
                        model.ContextWindow, model.Temperature, provider.ApiType);
                }
            }

            // Provider specified but model not found — still use the provider's baseUrl
            // (model might not be listed but the endpoint might serve it)
            if (Providers.TryGetValue(providerName, out provider))
            {
                return new ResolvedModel(
                    provider.BaseUrl, modelId, provider.ApiKey ?? "",
                    provider.DefaultContextWindow, provider.DefaultTemperature, provider.ApiType);
            }

            return null;
        }

        // Search all providers for the model id
        foreach (var (providerName, provider) in Providers)
        {
            var model = provider.Models.Find(m => m.Id == modelRef);
            if (model != null)
            {
                return new ResolvedModel(
                    provider.BaseUrl, model.Id, provider.ApiKey ?? "",
                    model.ContextWindow, model.Temperature, provider.ApiType);
            }
        }

        return null;
    }

    /// <summary>Get all available model ids across all providers.</summary>
    public List<(string Provider, string ModelId, string Name, int ContextWindow, string ApiType)> GetAllModels()
    {
        var result = new List<(string, string, string, int, string)>();
        foreach (var (providerName, provider) in Providers)
        {
            foreach (var model in provider.Models)
            {
                result.Add((providerName, model.Id, model.Name ?? model.Id, model.ContextWindow, provider.ApiType));
            }
        }
        return result;
    }

    // --- File I/O ---

    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".little_helper");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "models.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Load config from ~/.little_helper/models.json. Returns empty config if not found.</summary>
    public static ModelConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new ModelConfig();

        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<ModelConfig>(json, JsonOptions) ?? new ModelConfig();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to load {ConfigPath}: {ex.Message}");
            return new ModelConfig();
        }
    }

    /// <summary>Save config to ~/.little_helper/models.json. Creates directory if needed.</summary>
    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(ConfigPath, json);
    }

    /// <summary>Create a default config with an Ollama provider.</summary>
    public static ModelConfig CreateDefault()
    {
        return new ModelConfig
        {
            DefaultModel = "qwen3:14b",
            Providers = new Dictionary<string, ProviderConfig>
            {
                ["ollama"] = new()
                {
                    BaseUrl = "http://localhost:11434/v1",
                    ApiKey = "",
                    Models = new List<ModelEntry>
                    {
                        new() { Id = "qwen3:14b", Name = "Qwen3 14B", ContextWindow = 32768, Temperature = 0.3 },
                        new() { Id = "qwen3:8b", Name = "Qwen3 8B", ContextWindow = 32768, Temperature = 0.3 },
                        new() { Id = "llama3.1:8b", Name = "Llama 3.1 8B", ContextWindow = 32768, Temperature = 0.3 },
                    }
                }
            }
        };
    }
}

/// <summary>A provider (an API endpoint serving models).</summary>
class ProviderConfig
{
    public string BaseUrl { get; set; } = "";
    public string? ApiKey { get; set; }
    public List<ModelEntry> Models { get; set; } = new();

    /// <summary>Default context window for models not explicitly listed.</summary>
    public int DefaultContextWindow { get; set; } = 32768;

    /// <summary>Default temperature for models not explicitly listed.</summary>
    public double DefaultTemperature { get; set; } = 0.3;

    /// <summary>Optional extra HTTP headers to send with every request.</summary>
    public Dictionary<string, string>? Headers { get; set; }

    // --- Future: Anthropic API support ---
    // Currently little_helper only supports OpenAI-compatible /chat/completions endpoints.
    // To add Anthropic Messages API support, implement an AnthropicModelClient that speaks
    // the /v1/messages format (content blocks, thinking tags, etc.) and route based on this field.
    //
    // The routing logic in ConfigResolver.Resolve() should check ApiType:
    //   "openai" (default) → use ModelClient (current)
    //   "anthropic"        → use AnthropicModelClient (to be implemented)
    //
    // Anthropic API differences from OpenAI:
    //   - POST /v1/messages instead of /v1/chat/completions
    //   - Uses "content" array with typed blocks (text, tool_use, tool_result)
    //   - API key in x-api-key header instead of Authorization: Bearer
    //   - anthropic-version header required (e.g. "2023-06-01")
    //   - Different tool calling format (tool_use blocks, not function calls)
    //   - Streaming uses SSE with event types (message_start, content_block_start, etc.)
    //
    // Affected files when implementing:
    //   ModelConfig.cs  — route on ApiType in Resolve()
    //   ConfigResolver.cs — pass ApiType through to RunAgent
    //   Program.cs      — select ModelClient vs AnthropicModelClient based on ApiType
    //   AnthropicModelClient.cs (new) — /v1/messages API client
    //   Types.cs        — ChatMessage may need Anthropic-specific role handling
    //
    /// <summary>API protocol: "openai" (default) or "anthropic". Currently only "openai" is supported.</summary>
    public string ApiType { get; set; } = "openai";
}

/// <summary>A model entry within a provider.</summary>
class ModelEntry
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public int ContextWindow { get; set; } = 32768;
    public double Temperature { get; set; } = 0.3;
}

/// <summary>Resolved model ready for use: endpoint + model id + api key + settings + API type.</summary>
record ResolvedModel(string BaseUrl, string ModelId, string ApiKey, int ContextWindow, double Temperature, string ApiType = "openai");