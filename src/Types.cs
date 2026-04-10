using System.Text.Json;

namespace LittleHelper;

// --- Enums ---

/// <summary>
/// Agent FSM states. StateFlow research: explicit FSM yields 63.73% success vs 40.3% for ReAct.
/// </summary>
public enum AgentState
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
public record ToolCall(string Id, string Name, JsonElement Arguments);

/// <summary>
/// Result from executing a single tool.
/// </summary>
public record ToolResult(string Output, bool IsError, string? FilePath = null);

/// <summary>
/// Final result from an agent run.
/// ThinkingLog accumulates all model reasoning across steps for TUI display.
/// </summary>
public record AgentResult(bool Success, string Output, List<string> FilesChanged,
    List<string> ThinkingLog = null!, int TotalThinkingTokens = 0)
{
    public List<string> ThinkingLog { get; init; } = ThinkingLog ?? new List<string>();
}

/// <summary>
/// Parsed response from the model API.
/// ThinkingContent captures chain-of-thought from thinking models (Kimi K2.5, DeepSeek, etc.)
/// that return a "reasoning_content" or "thinking" field alongside the main content.
/// </summary>
public record ModelResponse(string Content, List<ToolCall> ToolCalls, int TokensUsed,
    string? ThinkingContent = null, int ThinkingTokens = 0);

/// <summary>
/// Result of context compaction.
/// </summary>
public record CompactionResult(List<ChatMessage> Messages, int TokensSaved);

/// <summary>
/// A discovered skill definition (from SKILL.md frontmatter).
/// </summary>
public record SkillDef(string Name, string Description, string FilePath);

// --- Chat Message Types ---

/// <summary>
/// A single message in the conversation. Different constructors for different roles.
/// Assistant messages may carry ReasoningContent from thinking models (Kimi K2.5, DeepSeek).
/// This MUST be serialized back as reasoning_content, or thinking-mode APIs reject the request.
/// </summary>
public record ChatMessage
{
    public string Role { get; init; }
    public string? Content { get; init; }
    public List<ToolCall>? ToolCalls { get; init; }
    public string? ToolCallId { get; init; }
    public ToolResult? ToolResult { get; init; }
    public string? ReasoningContent { get; init; }

    private ChatMessage(string role, string? content, List<ToolCall>? toolCalls,
                        string? toolCallId, ToolResult? toolResult,
                        string? reasoningContent = null)
    {
        Role = role;
        Content = content;
        ToolCalls = toolCalls;
        ToolCallId = toolCallId;
        ToolResult = toolResult;
        ReasoningContent = reasoningContent;
    }

    /// <summary>System message with instructions.</summary>
    public static ChatMessage System(string content) =>
        new("system", content, null, null, null);

    /// <summary>User message with a prompt.</summary>
    public static ChatMessage User(string content) =>
        new("user", content, null, null, null);

    /// <summary>Assistant message with optional text, tool calls, and reasoning.</summary>
    public static ChatMessage Assistant(string? content, List<ToolCall>? toolCalls = null,
        string? reasoningContent = null) =>
        new("assistant", content, toolCalls, null, null, reasoningContent);

    /// <summary>Tool result message tied to a specific tool call.</summary>
    public static ChatMessage FromToolResult(string toolCallId, ToolResult result) =>
        new("tool", result.Output, null, toolCallId, result);
}

// --- Configuration ---

/// <summary>
/// Agent configuration. Immutable — create new instances with `with` expressions.
/// </summary>
public record AgentConfig(
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
