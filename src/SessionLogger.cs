using System.Text;
using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// Writes a structured session log to ~/.little_helper/logs/.
/// Each session gets a timestamped JSONL file (one JSON object per line).
/// Lines: session_start, step, tool_call, tool_result, session_end.
/// Designed for later analysis, TUI replay, and debugging.
/// </summary>
public class SessionLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly string _logPath;
    private readonly DateTime _startTime;
    private bool _disposed;

    public string LogPath => _logPath;

    public SessionLogger(string model, string workingDir)
    {
        _startTime = DateTime.UtcNow;
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".little_helper", "logs");
        Directory.CreateDirectory(logDir);

        var timestamp = _startTime.ToString("yyyyMMdd_HHmmss");
        _logPath = Path.Combine(logDir, $"{timestamp}_{model.Replace('/', '_').Replace(':', '_')}.jsonl");
        _writer = new StreamWriter(_logPath, append: false, Encoding.UTF8) { AutoFlush = true };

        WriteRecord(new Dictionary<string, object?>
        {
            ["type"] = "session_start",
            ["timestamp"] = _startTime.ToString("O"),
            ["model"] = model,
            ["working_dir"] = workingDir,
        });
    }

    /// <summary>Log a model response (step).</summary>
    public void Step(int stepNum, int tokensUsed, int thinkingTokens, int toolCallCount,
        string? thinkingContent, string? contentPreview)
    {
        WriteRecord(new Dictionary<string, object?>
        {
            ["type"] = "step",
            ["step"] = stepNum,
            ["tokens"] = tokensUsed,
            ["thinking_tokens"] = thinkingTokens,
            ["tool_calls"] = toolCallCount,
            ["thinking"] = thinkingContent,
            ["preview"] = Truncate(contentPreview, 200),
        });
    }

    /// <summary>Log a tool call with its arguments and result.</summary>
    public void ToolCall(string toolName, string argsSummary, string resultPreview,
        bool isError, string? filePath, long durationMs)
    {
        WriteRecord(new Dictionary<string, object?>
        {
            ["type"] = "tool",
            ["tool"] = toolName,
            ["args"] = argsSummary,
            ["result"] = Truncate(resultPreview, 500),
            ["is_error"] = isError,
            ["file_path"] = filePath,
            ["duration_ms"] = durationMs,
        });
    }

    /// <summary>Log session end with final stats.</summary>
    public void End(bool success, int totalSteps, int totalTokens, int totalThinkingTokens,
        List<string> filesChanged)
    {
        WriteRecord(new Dictionary<string, object?>
        {
            ["type"] = "session_end",
            ["success"] = success,
            ["steps"] = totalSteps,
            ["total_tokens"] = totalTokens,
            ["thinking_tokens"] = totalThinkingTokens,
            ["files_changed"] = filesChanged,
            ["duration_sec"] = (DateTime.UtcNow - _startTime).TotalSeconds.ToString("F1"),
        });
    }

    /// <summary>Log an arbitrary message (errors, warnings, state transitions).</summary>
    public void Message(string message)
    {
        WriteRecord(new Dictionary<string, object?>
        {
            ["type"] = "message",
            ["text"] = message,
            ["elapsed_ms"] = (DateTime.UtcNow - _startTime).TotalMilliseconds.ToString("F0"),
        });
    }

    private void WriteRecord(Dictionary<string, object?> record)
    {
        try
        {
            _writer.WriteLine(JsonSerializer.Serialize(record,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        }
        catch { /* logging should never crash the agent */ }
    }

    private static string? Truncate(string? s, int maxLen) =>
        s == null ? null : s.Length <= maxLen ? s : s[..maxLen] + "...";

    public void Dispose()
    {
        if (!_disposed) { _writer.Dispose(); _disposed = true; }
    }
}

/// <summary>
/// Reads session JSONL logs back into typed records.
/// Used by the TUI's SessionManager to avoid duplicating the log format.
/// </summary>
public static class SessionLogReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>Read all entries from a session JSONL file.</summary>
    public static List<SessionEntry> ReadEntries(string logPath)
    {
        var entries = new List<SessionEntry>();
        if (!File.Exists(logPath)) return entries;

        foreach (var line in File.ReadLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<SessionEntry>(line, JsonOptions);
                if (entry != null) entries.Add(entry);
            }
            catch { /* skip malformed lines */ }
        }
        return entries;
    }

    /// <summary>List all session log files in ~/.little_helper/logs/, newest first.</summary>
    public static List<string> ListLogFiles()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".little_helper", "logs");
        if (!Directory.Exists(logDir)) return new();

        return Directory.GetFiles(logDir, "*.jsonl")
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .ToList();
    }
}

/// <summary>
/// A single entry from a session JSONL log.
/// Type can be: session_start, step, tool, message, session_end.
/// </summary>
public record SessionEntry
{
    public string? Type { get; init; }
    public string? Timestamp { get; init; }
    public string? Model { get; init; }
    public string? WorkingDir { get; init; }

    // Step fields
    public int? Step { get; init; }
    public int? Tokens { get; init; }
    public int? ThinkingTokens { get; init; }
    public int? ToolCalls { get; init; }
    public string? Thinking { get; init; }
    public string? Preview { get; init; }

    // Tool fields
    public string? Tool { get; init; }
    public string? Args { get; init; }
    public string? Result { get; init; }
    public bool? IsError { get; init; }
    public string? FilePath { get; init; }
    public long? DurationMs { get; init; }

    // Message fields
    public string? Text { get; init; }
    public string? ElapsedMs { get; init; }

    // Session end fields
    public bool? Success { get; init; }
    public int? Steps { get; init; }
    public int? TotalTokens { get; init; }
    public List<string>? FilesChanged { get; init; }
    public string? DurationSec { get; init; }
}