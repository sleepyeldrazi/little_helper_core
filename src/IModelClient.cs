namespace LittleHelper;

/// <summary>
/// Abstraction over model API clients. Both ModelClient (OpenAI) and AnthropicClient
/// implement this so the Agent doesn't need to know which API format is in use.
/// </summary>
public interface IModelClient : IDisposable
{
    /// <summary>Send a chat completion request. Returns parsed response with content and/or tool calls.</summary>
    Task<ModelResponse> Complete(List<ChatMessage> messages, CancellationToken ct = default,
        int maxRetries = 3, IAgentObserver? observer = null, bool enableStreaming = false);

    /// <summary>Total tokens consumed across all calls in this session.</summary>
    int TotalTokensUsed { get; }

    /// <summary>Total thinking/reasoning tokens consumed across all calls.</summary>
    int TotalThinkingTokens { get; }

    /// <summary>All thinking/reasoning content captured this session.</summary>
    List<string> ThinkingLog { get; }

    /// <summary>Register a tool that the model can call.</summary>
    void RegisterTool(string name, string description, System.Text.Json.JsonElement parametersSchema);
}