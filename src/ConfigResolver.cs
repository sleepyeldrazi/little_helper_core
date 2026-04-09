namespace LittleHelper;

/// <summary>
/// Resolves CLI arguments against model config from ~/.little_helper/models.json.
/// Extracted from Program.cs to keep files under 300 lines (Rule #8).
/// </summary>
static class ConfigResolver
{
    /// <summary>
    /// Resolve CLI args + config file into final values for endpoint, model, api key, etc.
    /// Priority: CLI arg > config file > hardcoded defaults.
    /// </summary>
    public static ResolvedConfig Resolve(
        string modelArg, string endpointArg, int maxContextArg, double temperatureArg,
        ModelConfig modelConfig)
    {
        // Determine which model id to use: CLI arg > config default > hardcoded
        string modelId;
        if (!string.IsNullOrEmpty(modelArg))
            modelId = modelArg;
        else if (!string.IsNullOrEmpty(modelConfig.DefaultModel))
            modelId = modelConfig.DefaultModel;
        else
            modelId = "qwen3:14b";

        // Try to resolve from config file
        var resolved = modelConfig.Resolve(modelId);

        if (resolved != null)
        {
            var endpoint = string.IsNullOrEmpty(endpointArg) ? resolved.BaseUrl : endpointArg;
            var apiKey = resolved.ApiKey;
            var contextWindow = maxContextArg > 0 ? maxContextArg : resolved.ContextWindow;
            var temperature = temperatureArg > 0 ? temperatureArg : resolved.Temperature;

            // Find provider headers
            Dictionary<string, string>? headers = null;
            foreach (var (_, prov) in modelConfig.Providers)
            {
                if (prov.BaseUrl.TrimEnd('/') == resolved.BaseUrl.TrimEnd('/') && prov.Headers != null)
                {
                    headers = prov.Headers;
                    break;
                }
            }

            // Warn if using an unsupported API type
            if (resolved.ApiType != "openai")
            {
                Console.Error.WriteLine(
                    $"Warning: Provider uses '{resolved.ApiType}' API, which is not yet supported. " +
                    "Only OpenAI-compatible endpoints work currently. " +
                    "See ProviderConfig.ApiType docs in ModelConfig.cs for implementation notes.");
            }

            return new ResolvedConfig(modelId, endpoint, apiKey, headers, contextWindow, temperature, resolved.ApiType);
        }

        // Not in config — use CLI defaults or hardcoded defaults
        var fallbackEndpoint = string.IsNullOrEmpty(endpointArg) ? "http://localhost:11434/v1" : endpointArg;
        var fallbackContext = maxContextArg > 0 ? maxContextArg : 32768;
        var fallbackTemp = temperatureArg > 0 ? temperatureArg : 0.3;
        return new ResolvedConfig(modelId, fallbackEndpoint, null, null, fallbackContext, fallbackTemp);
    }
}

/// <summary>Final resolved config values ready for AgentConfig construction.</summary>
record ResolvedConfig(
    string ModelId,
    string Endpoint,
    string? ApiKey,
    Dictionary<string, string>? Headers,
    int ContextWindow,
    double Temperature,
    string ApiType = "openai");