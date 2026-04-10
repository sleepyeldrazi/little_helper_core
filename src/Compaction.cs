using System.Text;

namespace LittleHelper;

/// <summary>
/// Context window management via observation masking.
/// Research: JetBrains observation masking beats LLM summarization (2.6% higher solve, 52% cheaper).
/// Strategy: Replace old tool outputs with placeholders, keep reasoning intact.
/// </summary>
public class Compaction
{
    private readonly AgentConfig _config;

    // Threshold to start compaction (80% of max context)
    private const double CompactionThreshold = 0.8;

    // Number of recent turns to always preserve
    private const int PreserveRecentTurns = 3;

    // If a single response exceeds this % of context, truncate it
    private const double OversizedThreshold = 0.5;

    public Compaction(AgentConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Compact messages if needed. Returns the compacted message list and tokens saved.
    /// </summary>
    public CompactionResult Compact(List<ChatMessage> messages)
    {
        if (messages.Count == 0)
            return new CompactionResult(messages, 0);

        var currentTokens = EstimateTokens(messages);
        var threshold = (int)(_config.MaxContextTokens * CompactionThreshold);

        if (currentTokens <= threshold)
            return new CompactionResult(messages, 0);

        // Need to compact
        var compacted = new List<ChatMessage>();
        int tokensSaved = 0;

        // Find the boundary: which messages to keep vs compress
        var (preserveCount, oversizedMessageIndex) = FindPreserveBoundary(messages, currentTokens);

        // Handle oversized single message (only allowed truncation case)
        if (oversizedMessageIndex >= 0)
        {
            var truncated = TruncateOversizedMessage(messages[oversizedMessageIndex], currentTokens);
            for (int i = 0; i < messages.Count; i++)
            {
                if (i == oversizedMessageIndex)
                    compacted.Add(truncated);
                else
                    compacted.Add(messages[i]);
            }
            tokensSaved = EstimateTokens(messages) - EstimateTokens(compacted);
            return new CompactionResult(compacted, tokensSaved);
        }

        // Standard observation masking
        var messagesToPreserve = Math.Min(preserveCount, messages.Count);
        var messagesToCompact = messages.Count - messagesToPreserve;

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];

            // Always preserve system message
            if (msg.Role == "system")
            {
                compacted.Add(msg);
                continue;
            }

            // Preserve recent messages
            if (i >= messagesToCompact)
            {
                compacted.Add(msg);
                continue;
            }

            // Compress middle messages based on role
            var compressed = CompressMessage(msg);
            compacted.Add(compressed);
            if (compressed.Content != msg.Content)
            {
                tokensSaved += EstimateTokens(msg) - EstimateTokens(compressed);
            }
        }

        return new CompactionResult(compacted, tokensSaved);
    }

    /// <summary>
    /// Find how many messages to preserve from the end, and check for oversized single message.
    /// Counts real user turns (from original prompt), not synthetic system messages
    /// injected during error recovery.
    /// </summary>
    private (int preserveCount, int oversizedIndex) FindPreserveBoundary(List<ChatMessage> messages, int totalTokens)
    {
        // Count backwards to find at least PreserveRecentTurns complete turns.
        // A turn boundary is when the model responds (assistant message with tool calls).
        // This avoids counting injected system messages (error recovery) as real turns.
        int preserveCount = 0;
        int turnsFound = 0;

        for (int i = messages.Count - 1; i >= 0; i--)
        {
            preserveCount++;
            // A "turn" ends when we see an assistant message (model response)
            if (messages[i].Role == "assistant")
            {
                turnsFound++;
                if (turnsFound >= PreserveRecentTurns)
                    break;
            }
        }

        // Check for oversized single message
        for (int i = 0; i < messages.Count - preserveCount; i++)
        {
            var msg = messages[i];
            if (msg.Role == "tool" && msg.Content != null)
            {
                var msgTokens = EstimateTokens(msg);
                if (msgTokens > totalTokens * OversizedThreshold)
                {
                    return (preserveCount, i);
                }
            }
        }

        return (preserveCount, -1);
    }

    /// <summary>
    /// Compress a single message based on its role.
    /// </summary>
    private ChatMessage CompressMessage(ChatMessage msg)
    {
        return msg.Role switch
        {
            // Keep assistant reasoning intact
            "assistant" => msg,

            // Replace tool results with placeholders
            "tool" => CompressToolResult(msg),

            // Compress user messages if they're very long (unlikely)
            "user" => CompressUserMessage(msg),

            // Default: keep as-is
            _ => msg
        };
    }

    /// <summary>
    /// Replace tool output with a placeholder describing what was called.
    /// Code-aware: preserves function signatures, class definitions, imports,
    /// and type definitions. Only masks function bodies and verbose output.
    /// </summary>
    private ChatMessage CompressToolResult(ChatMessage msg)
    {
        if (msg.ToolResult == null || msg.Content == null)
            return msg;

        var filePath = msg.ToolResult.FilePath;
        var lineCount = msg.Content.Split('\n').Length;

        // Try code-aware compression for file reads (tool results with FilePath)
        if (!string.IsNullOrEmpty(filePath) && CodeCompressor.IsCodeFile(filePath))
        {
            var compressed = CodeCompressor.Compress(msg.Content);
            if (compressed != null)
            {
                return ChatMessage.FromToolResult(msg.ToolCallId ?? "", new ToolResult(
                    Output: compressed,
                    IsError: msg.ToolResult.IsError,
                    FilePath: filePath));
            }
        }

        // Fallback: generic placeholder
        string placeholder;
        if (!string.IsNullOrEmpty(filePath))
        {
            var relativePath = Path.GetFileName(filePath);
            placeholder = $"[Output of previous operation on {relativePath} — {lineCount} lines]";
        }
        else
        {
            placeholder = $"[Tool output — {lineCount} lines, {msg.Content.Length} chars]";
        }

        return ChatMessage.FromToolResult(msg.ToolCallId ?? "", new ToolResult(
            Output: placeholder,
            IsError: msg.ToolResult.IsError,
            FilePath: filePath));
    }

    /// <summary>
    /// Compress user message if extremely long (rare).
    /// </summary>
    private ChatMessage CompressUserMessage(ChatMessage msg)
    {
        if (msg.Content == null || msg.Content.Length < 2000)
            return msg;

        // Truncate with note
        var truncated = msg.Content[..2000] + "\n... [truncated in compaction]";
        return ChatMessage.User(truncated);
    }

    /// <summary>
    /// Truncate a single oversized message (fallback for oversized responses).
    /// Handles all message roles including user — if it's oversized and
    /// in the compactable zone, it gets truncated.
    /// </summary>
    private ChatMessage TruncateOversizedMessage(ChatMessage msg, int totalTokens)
    {
        if (msg.Content == null)
            return msg;

        var maxChars = (int)(_config.MaxContextTokens * OversizedThreshold * 3); // chars/3 for code
        if (msg.Content.Length <= maxChars)
            return msg;

        var truncated = msg.Content[..maxChars] +
            $"\n... [truncated: was {msg.Content.Length} chars, exceeded 50% of context window]";

        if (msg.Role == "tool" && msg.ToolResult != null)
        {
            return ChatMessage.FromToolResult(
                msg.ToolCallId ?? "",
                new ToolResult(truncated, msg.ToolResult.IsError, msg.ToolResult.FilePath));
        }

        // Handle oversized user messages too
        if (msg.Role == "user")
        {
            return ChatMessage.User(truncated);
        }

        // For assistant or other roles, reconstruct with truncated content
        if (msg.Role == "assistant")
            return ChatMessage.Assistant(truncated, msg.ToolCalls);
        if (msg.Role == "system")
            return ChatMessage.System(truncated);
        // Fallback: reconstruct user message (shouldn't reach here normally)
        return ChatMessage.User(truncated);
    }

    /// <summary>Estimate tokens for a message: chars/4 for English, chars/3 for code.</summary>
    public static int EstimateTokens(ChatMessage msg)
    {
        if (msg.Content == null)
            return 4;
        var divisor = IsCodeContent(msg.Content) ? 3 : 4;
        return (msg.Content.Length / divisor) + 4;
    }

    /// <summary>Estimate total tokens for a list of messages.</summary>
    public static int EstimateTokens(List<ChatMessage> messages) => messages.Sum(EstimateTokens);

    /// <summary>Check if content appears to be code (3+ code indicators).</summary>
    private static bool IsCodeContent(string content)
    {
        var codeIndicators = new[] { "{", "}", ";", "//", "/*", "*/", "def ", "func ", "class ", "public ", "private " };
        return codeIndicators.Count(i => content.Contains(i)) >= 3;
    }

    /// <summary>Check if compaction is needed (tokens exceed 80% threshold).</summary>
    public bool NeedsCompaction(List<ChatMessage> messages)
        => EstimateTokens(messages) > (int)(_config.MaxContextTokens * CompactionThreshold);

    /// <summary>Get current token count for display/logging.</summary>
    public int GetTokenCount(List<ChatMessage> messages) => EstimateTokens(messages);
}

/// <summary>
/// Extension methods for compaction integration with Agent.
/// </summary>
public static class CompactionExtensions
{
    /// <summary>
    /// Compact messages if needed, logging the result.
    /// </summary>
    public static CompactionResult CompactIfNeeded(this Compaction compactor, List<ChatMessage> messages)
    {
        var beforeTokens = Compaction.EstimateTokens(messages);
        var result = compactor.Compact(messages);
        var afterTokens = Compaction.EstimateTokens(result.Messages);

        if (result.TokensSaved > 0)
        {
            // Compaction info is reported via IAgentObserver.OnCompaction by the Agent
        }

        return result;
    }
}