using System.Text.Json;
using LittleHelper;

namespace LittleHelper.Tests;

/// <summary>
/// Integration test: makes a real HTTP call to a model endpoint.
/// Requires a running model server (Ollama by default).
/// Skip with `dotnet test --filter "RequiresModel!=true"` to skip.
/// </summary>
public class ModelIntegrationTests
{
    /// <summary>
    /// Test that the agent can get a response from a real model.
    /// Uses the config in ~/.little_helper/models.json to find an endpoint.
    /// This test is skipped if no model server is available.
    /// </summary>
    [Fact(Skip = "Requires running model server")]
    public async Task ModelClient_RealEndpoint_GetsResponse()
    {
        // Load config to find an available endpoint
        var config = ModelConfig.Load();
        var resolved = config.Resolve(config.DefaultModel);

        string endpoint;
        string model;
        string? apiKey;
        Dictionary<string, string>? headers;

        if (resolved != null)
        {
            endpoint = resolved.BaseUrl;
            model = resolved.ModelId;
            apiKey = resolved.ApiKey;
            headers = null;

            // Find provider headers
            foreach (var (_, prov) in config.Providers)
            {
                if (prov.BaseUrl.TrimEnd('/') == resolved.BaseUrl.TrimEnd('/') && prov.Headers != null)
                {
                    headers = prov.Headers;
                    break;
                }
            }
        }
        else
        {
            // Fallback to localhost Ollama
            endpoint = "http://localhost:11434/v1";
            model = "qwen3:14b";
            apiKey = null;
            headers = null;
        }

        using var client = new ModelClient(endpoint, model, 0.3, apiKey, headers);
        ToolSchemas.RegisterAll(client);

        var messages = new List<ChatMessage>
        {
            ChatMessage.System("You are a helpful assistant. Respond with exactly one word."),
            ChatMessage.User("What is 2+2?"),
        };

        var response = await client.Complete(messages, CancellationToken.None, maxRetries: 1);

        // We got some response (might be an error if model not available, that's OK)
        Assert.NotNull(response);
        Assert.NotEqual("ERROR: No response from model", response.Content);
        Assert.NotEqual("ERROR: Max retries exceeded", response.Content);

        Console.Error.WriteLine($"[Integration] Model response: {response.Content}");
        Console.Error.WriteLine($"[Integration] Tokens used: {response.TokensUsed}");
        Console.Error.WriteLine($"[Integration] Tool calls: {response.ToolCalls.Count}");
    }

    [Fact(Skip = "Requires running model server")]
    public async Task Agent_FullLoop_WithRealModel()
    {
        var config = ModelConfig.Load();
        var resolved = config.Resolve(config.DefaultModel);

        string endpoint;
        string model;
        string? apiKey;

        if (resolved != null)
        {
            endpoint = resolved.BaseUrl;
            model = resolved.ModelId;
            apiKey = resolved.ApiKey;
        }
        else
        {
            endpoint = "http://localhost:11434/v1";
            model = "qwen3:14b";
            apiKey = null;
        }

        // Find headers for the provider
        Dictionary<string, string>? headers = null;
        if (resolved != null)
        {
            foreach (var (_, prov) in config.Providers)
            {
                if (prov.BaseUrl.TrimEnd('/') == resolved.BaseUrl.TrimEnd('/') && prov.Headers != null)
                {
                    headers = prov.Headers;
                    break;
                }
            }
        }

        var agentConfig = new AgentConfig(
            ModelEndpoint: endpoint,
            ModelName: model,
            MaxContextTokens: resolved?.ContextWindow ?? 32768,
            MaxSteps: 5,
            MaxRetries: 1,
            StallThreshold: 3,
            WorkingDirectory: Path.GetTempPath(),
            Temperature: 0.3,
            ApiKey: apiKey,
            ExtraHeaders: headers);

        using var modelClient = new ModelClient(endpoint, model, 0.3, apiKey, headers);
        ToolSchemas.RegisterAll(modelClient);

        var toolExecutor = new ToolExecutor(agentConfig.WorkingDirectory);
        var skills = new SkillDiscovery();

        var agent = new Agent(agentConfig, modelClient, toolExecutor, skills);
        var result = await agent.RunAsync("What is the capital of France? Answer in one word.", CancellationToken.None);

        Assert.NotNull(result);
        // The model should respond (success or stall — either is fine for this test,
        // we're just verifying the full loop doesn't crash)
        Console.Error.WriteLine($"[Integration] Agent result: Success={result.Success}, Output={result.Output}");
    }
}