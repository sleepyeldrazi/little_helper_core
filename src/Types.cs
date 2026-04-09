using System.Text.Json;

namespace LittleHelper;

// --- Enums ---

/// <summary>
/// Agent FSM states. StateFlow research: explicit FSM yields 63.73% success vs 40.3% for ReAct.
/// </summary>
enum AgentState
{
    Planning,
    Executing,
    Observing,
    ErrorRecovery,
    Done
}

// --- Records ---

/// <summary>
/// A parsed tool call from the model's response.
/// </summary>
record ToolCall(string Id, string Name, JsonElement Arguments);

/// <summary>
/// Result from executing a single tool.
/// </summary>
record ToolResult(string Output, bool IsError, string? FilePath = null);

/// <summary>
/// Final result from an agent run.
/// ThinkingLog accumulates all model reasoning across steps for TUI display.
/// </summary>
record AgentResult(bool Success, string Output, List<string> FilesChanged,
    List<string> ThinkingLog = null!, int TotalThinkingTokens = 0)
{
    public List<string> ThinkingLog { get; init; } = ThinkingLog ?? new List<string>();
}

/// <summary>
/// Parsed response from the model API.
/// ThinkingContent captures chain-of-thought from thinking models (Kimi K2.5, DeepSeek, etc.)
/// that return a "reasoning_content" or "thinking" field alongside the main content.
/// </summary>
record ModelResponse(string Content, List<ToolCall> ToolCalls, int TokensUsed,
    string? ThinkingContent = null, int ThinkingTokens = 0);

/// <summary>
/// Result of context compaction.
/// </summary>
record CompactionResult(List<ChatMessage> Messages, int TokensSaved);

/// <summary>
/// A discovered skill definition (from SKILL.md frontmatter).
/// </summary>
record SkillDef(string Name, string Description, string FilePath);

// --- Chat Message Types ---

/// <summary>
/// A single message in the conversation. Different constructors for different roles.
/// </summary>
record ChatMessage
{
    public string Role { get; init; }
    public string? Content { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public ToolResult? ToolResult { get; init; }

    private ChatMessage(string role, string? content, List<ToolCall>? toolCalls,
                        string? toolCallId, ToolResult? toolResult)
    {
        Role = role;
        Content = content;
        ToolCalls = toolCalls;
        ToolCallId = toolCallId;
        ToolResult = toolResult;
    }

    /// <summary>System message with instructions.</summary>
    public static ChatMessage System(string content) =>
        new("system", content, null, null, null);

    /// <summary>User message with a prompt.</summary>
    public static ChatMessage User(string content) =>
        new("user", content, null, null, null);

    /// <summary>Assistant message with optional text and tool calls.</summary>
    public static ChatMessage Assistant(string? content, List<ToolCall>? toolCalls = null) =>
        new("assistant", content, toolCalls, null, null);

    /// <summary>Tool result message tied to a specific tool call.</summary>
    public static ChatMessage FromToolResult(string toolCallId, ToolResult result) =>
        new("tool", result.Output, null, toolCallId, result);
}

// --- Configuration ---

/// <summary>
/// Agent configuration. Immutable — create new instances with `with` expressions.
/// </summary>
record AgentConfig(
    string ModelEndpoint,      // e.g. "http://localhost:11434/v1"
    string ModelName,          // e.g. "qwen3:14b"
    int MaxContextTokens,      // e.g. 32768
    int MaxSteps,              // e.g. 30
    int MaxRetries,            // e.g. 2
    int StallThreshold,        // e.g. 5
    string WorkingDirectory,
    double Temperature = 0.3,  // model sampling temperature
    string? ApiKey = null,     // optional API key for cloud providers
    Dictionary<string, string>? ExtraHeaders = null  // optional extra HTTP headers
)
{
    /// <summary>Default configuration for local models.</summary>
    public static AgentConfig Default => new(
        ModelEndpoint: "http://localhost:11434/v1",
        ModelName: "qwen3:14b",
        MaxContextTokens: 32768,
        MaxSteps: 30,
        MaxRetries: 2,
        StallThreshold: 5,
        WorkingDirectory: Directory.GetCurrentDirectory()
    );
}
