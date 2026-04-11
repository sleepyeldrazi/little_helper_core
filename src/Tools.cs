using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// 5 tool implementations: read, run, write, search, bash.
/// Each is a simple function: arguments -> ToolResult.
/// All paths are resolved relative to the working directory.
/// Tilde (~) expansion is supported — ~/ resolves to the user's home directory.
/// Path escape check is enforced for write (destructive) but relaxed for read/search.
/// </summary>
public class ToolExecutor
{
    private readonly string _workingDir;
    private readonly HashSet<string> _destructiveCommands;
    private readonly bool _blockDestructive;

    /// <summary>
    /// Sub-agent spawn manager. Handles tmux session lifecycle and spawn logging.
    /// When set, takes priority over SpawnHandler. Set by the TUI to enable sub-agents.
    /// </summary>
    public SpawnManager? SpawnManager { get; set; }

    /// <summary>
    /// Sub-agent spawn delegate. Legacy wiring for TUI-based spawning.
    /// Arguments: (task, type) -> result string.
    /// Used only when SpawnManager is null. When both are null, returns an error.
    /// </summary>
    public Func<string, string, Task<ToolResult>>? SpawnHandler { get; set; }

    private bool _allowEscape;

    public ToolExecutor(string workingDirectory, bool blockDestructive = false, bool allowEscape = false)
    {
        _workingDir = Path.GetFullPath(workingDirectory);
        _blockDestructive = blockDestructive;
        _allowEscape = allowEscape;

        _destructiveCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rm", "rmdir", "del", "format", "mkfs", "dd",
            "shutdown", "reboot", "poweroff",
            "chmod", "chown", "systemctl", "service",
            "apt", "yum", "dnf", "pip",
            "sudo", "doas"
        };
    }

    /// <summary>Enable/disable path escape protection (yolo mode).</summary>
    public void SetAllowEscape(bool allow) => _allowEscape = allow;

    /// <summary>Dispatch a tool call by name. Returns the tool result.</summary>
    public async Task<ToolResult> Execute(string toolName, JsonElement arguments)
    {
        // Validation guard: catch malformed args before execution
        var validationError = Validate(toolName, arguments);
        if (validationError != null)
            return new ToolResult(validationError, IsError: true);

        return toolName.ToLowerInvariant() switch
        {
            "read" => await Read(arguments),
            "run" => await Run(arguments),
            "write" => await Write(arguments),
            "edit" => await Edit(arguments),
            "search" => await SearchAsync(arguments),
            "bash" => await Run(arguments),  // bash is alias for run
            "spawn" => await Spawn(arguments), // delegate to sub-agent in tmux
            "patch" => await Edit(arguments),  // patch is alias for edit
            _ => new ToolResult($"Unknown tool: {toolName}", IsError: true)
        };
    }

    /// <summary>
    /// Validate tool call arguments before execution.
    /// Catches: missing required fields, wrong types, nonexistent paths for read.
    /// Returns null if valid, error message if invalid.
    /// </summary>
    private string? Validate(string toolName, JsonElement args)
    {
        var name = toolName.ToLowerInvariant();

        // Check required fields per tool
        return name switch
        {
            "read" => ValidateRead(args),
            "write" => ValidateWrite(args),
            "edit" or "patch" => ValidateEdit(args),
            "run" or "bash" => ValidateRun(args),
            "search" => ValidateSearch(args),
            "spawn" => ValidateSpawn(args),
            _ => null // Unknown tools are caught by Execute
        };
    }

    private static string? ValidateRead(JsonElement args)
    {
        if (!args.TryGetProperty("path", out var pathProp))
            return "Missing required argument 'path' for read tool.";
        if (pathProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(pathProp.GetString()))
            return "Argument 'path' must be a non-empty string.";
        // Don't check file existence here — Read() already handles that with a clear message
        if (args.TryGetProperty("offset", out var offset) && offset.ValueKind != JsonValueKind.Number)
            return "Argument 'offset' must be a number.";
        if (args.TryGetProperty("limit", out var limit) && limit.ValueKind != JsonValueKind.Number)
            return "Argument 'limit' must be a number.";
        return null;
    }

    private static string? ValidateWrite(JsonElement args)
    {
        if (!args.TryGetProperty("path", out var pathProp))
            return "Missing required argument 'path' for write tool.";
        if (pathProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(pathProp.GetString()))
            return "Argument 'path' must be a non-empty string.";
        if (!args.TryGetProperty("content", out _))
            return "Missing required argument 'content' for write tool.";
        return null;
    }

    private static string? ValidateEdit(JsonElement args)
    {
        if (!args.TryGetProperty("path", out var pathProp))
            return "Missing required argument 'path' for edit tool.";
        if (pathProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(pathProp.GetString()))
            return "Argument 'path' must be a non-empty string.";
        if (!args.TryGetProperty("old_string", out var oldProp))
            return "Missing required argument 'old_string' for edit tool.";
        if (oldProp.ValueKind != JsonValueKind.String)
            return "Argument 'old_string' must be a string.";
        if (!args.TryGetProperty("new_string", out var newProp))
            return "Missing required argument 'new_string' for edit tool.";
        if (newProp.ValueKind != JsonValueKind.String)
            return "Argument 'new_string' must be a string.";
        return null;
    }

    private static string? ValidateRun(JsonElement args)
    {
        if (!args.TryGetProperty("command", out var cmdProp))
            return "Missing required argument 'command' for run tool.";
        if (cmdProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(cmdProp.GetString()))
            return "Argument 'command' must be a non-empty string.";
        if (args.TryGetProperty("timeout", out var timeout) && timeout.ValueKind != JsonValueKind.Number)
            return "Argument 'timeout' must be a number.";
        return null;
    }

    private static string? ValidateSearch(JsonElement args)
    {
        if (!args.TryGetProperty("pattern", out var patProp))
            return "Missing required argument 'pattern' for search tool.";
        if (patProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(patProp.GetString()))
            return "Argument 'pattern' must be a non-empty string.";
        return null;
    }

    private static string? ValidateSpawn(JsonElement args)
    {
        if (!args.TryGetProperty("task", out var taskProp))
            return "Missing required argument 'task' for spawn tool.";
        if (taskProp.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(taskProp.GetString()))
            return "Argument 'task' must be a non-empty string.";
        if (args.TryGetProperty("type", out var typeProp))
        {
            var typeVal = typeProp.GetString();
            if (typeVal != "small" && typeVal != "complex")
                return "Argument 'type' must be 'small' or 'complex'.";
        }
        return null;
    }

    /// <summary>Read file contents. Honors offset/limit. Never truncates (Rule #6).</summary>
    private Task<ToolResult> Read(JsonElement args)
    {
        // Read allows paths outside working dir (e.g. ~/.little_helper/models.json)
        var path = ResolvePath(args.GetProperty("path").GetString()!, allowEscape: true);
        int offset = args.TryGetProperty("offset", out var o) ? o.GetInt32() : 1;
        int limit = args.TryGetProperty("limit", out var l) ? l.GetInt32() : 0;

        if (!File.Exists(path))
            return Task.FromResult(new ToolResult($"File not found: {path}", IsError: true, FilePath: path));

        try
        {
            var lines = File.ReadAllLines(path);
            int startLine = Math.Max(0, Math.Min(offset - 1, lines.Length - 1));
            int availableLines = lines.Length - startLine;
            int count = limit > 0 ? Math.Min(limit, availableLines) : availableLines;
            count = Math.Max(0, count);
            int endLine = startLine + count;

            var output = new StringBuilder();
            output.AppendLine($"File: {GetDisplayPath(path)} ({lines.Length} lines total)");

            for (int i = startLine; i < endLine; i++)
                output.AppendLine($"{i + 1,6}|{lines[i]}");

            // Truncation awareness: tell the model exactly what's missing
            if (startLine > 0 && endLine < lines.Length)
                output.AppendLine($"[Showing lines {startLine + 1}-{endLine} of {lines.Length}. Lines 1-{startLine} are above, lines {endLine + 1}-{lines.Length} are below. Use read with offset/limit to see more.]");
            else if (startLine > 0)
                output.AppendLine($"[Showing lines {startLine + 1}-{endLine} of {lines.Length}. Lines 1-{startLine} are above. Use read with offset=1 to see from the start.]");
            else if (endLine < lines.Length)
                output.AppendLine($"[Showing lines {startLine + 1}-{endLine} of {lines.Length}. Lines {endLine + 1}-{lines.Length} are below. Use read with offset={endLine + 1} to see the rest.]");

            return Task.FromResult(new ToolResult(output.ToString(), IsError: false, FilePath: path));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new ToolResult($"Error reading file: {ex.Message}", IsError: true, FilePath: path));
        }
    }

    /// <summary>
    /// Execute shell command via stdin pipe (avoids shell injection).
    /// Default timeout 60s. Full output, no truncation.
    /// </summary>
    private async Task<ToolResult> Run(JsonElement args)
    {
        var command = args.GetProperty("command").GetString()!;
        int timeoutSec = args.TryGetProperty("timeout", out var t) ? t.GetInt32() : 60;

        if (_blockDestructive && IsDestructive(command))
            return new ToolResult(
                $"Command blocked (destructive): {command}. Set blockDestructive=false to allow.",
                IsError: true);

        var result = await ShellExecutor.RunViaStdinAsync(command, _workingDir, timeoutSec);
        return FormatShellResult(result);
    }

    /// <summary>Write content to file. Creates parent directories. No parsing (Rule #3).</summary>
    private async Task<ToolResult> Write(JsonElement args)
    {
        // Write enforces path escape check — can only write inside working dir
        var path = ResolvePath(args.GetProperty("path").GetString()!, allowEscape: false);
        var content = args.GetProperty("content").GetString() ?? "";

        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(path, content);
            var bytes = Encoding.UTF8.GetByteCount(content);
            return new ToolResult($"Wrote {bytes} bytes to {GetDisplayPath(path)}", IsError: false, FilePath: path);
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error writing file: {ex.Message}", IsError: true, FilePath: path);
        }
    }

    /// <summary>
    /// Edit a file by replacing old_string with new_string.
    /// old_string must be unique in the file (fails if ambiguous).
    /// Use replace_all for non-unique matches. More efficient than rewrite for small changes.
    /// </summary>
    private async Task<ToolResult> Edit(JsonElement args)
    {
        // Edit enforces path escape check — same as write
        var path = ResolvePath(args.GetProperty("path").GetString()!, allowEscape: false);
        var oldString = args.GetProperty("old_string").GetString() ?? "";
        var newString = args.GetProperty("new_string").GetString() ?? "";
        var replaceAll = args.TryGetProperty("replace_all", out var ra) && ra.ValueKind == JsonValueKind.True;

        if (!File.Exists(path))
            return new ToolResult($"File not found: {path}", IsError: true, FilePath: path);

        if (string.IsNullOrEmpty(oldString))
            return new ToolResult("old_string cannot be empty. Use the write tool to create or overwrite files.", IsError: true);

        try
        {
            var content = await File.ReadAllTextAsync(path);

            // Check if old_string exists in file
            if (!content.Contains(oldString))
            {
                // Try to help with a fuzzy hint
                var searchLines = oldString.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (searchLines.Length > 0)
                {
                    var firstLine = searchLines[0].Trim();
                    var matches = content.Split('\n')
                        .Select((line, idx) => (line, idx))
                        .Where(t => t.line.Contains(firstLine))
                        .Take(3)
                        .ToList();

                    if (matches.Count > 0)
                    {
                        var hint = string.Join("\n", matches.Select(m => $"  Line {m.idx + 1}: {m.line.Trim()}"));
                        return new ToolResult(
                            $"old_string not found in {GetDisplayPath(path)}.\n" +
                            $"Similar lines found (the exact whitespace/indentation may differ):\n{hint}\n\n" +
                            $"Tip: Use the read tool to see the exact content, then retry with the exact text.",
                            IsError: true, FilePath: path);
                    }
                }

                return new ToolResult(
                    $"old_string not found in {GetDisplayPath(path)}. " +
                    $"Use the read tool to see the current file content.",
                    IsError: true, FilePath: path);
            }

            // Check uniqueness (unless replace_all)
            if (!replaceAll)
            {
                var count = CountOccurrences(content, oldString);
                if (count > 1)
                {
                    return new ToolResult(
                        $"old_string found {count} times in {GetDisplayPath(path)}. " +
                        $"It must be unique for a single replacement. " +
                        $"Either include more surrounding context to make it unique, " +
                        $"or set replace_all to true to replace all occurrences.",
                        IsError: true, FilePath: path);
                }
            }

            // Perform replacement
            var newContent = replaceAll
                ? content.Replace(oldString, newString)
                : ReplaceFirst(content, oldString, newString);

            await File.WriteAllTextAsync(path, newContent);

            // Build summary
            var oldLines = oldString.Split('\n').Length;
            var newLines = newString.Split('\n').Length;
            var action = replaceAll ? $"Replaced {CountOccurrences(content, oldString)} occurrences" : "Replaced 1 occurrence";
            var displayPath = GetDisplayPath(path);
            var summary = newString.Length == 0
                ? $"{action} (deleted {oldLines} lines) in {displayPath}"
                : newString.Length < oldString.Length
                    ? $"{action} ({oldLines} -> {newLines} lines) in {displayPath}"
                    : $"{action} ({oldLines} -> {newLines} lines) in {displayPath}";

            return new ToolResult(summary, IsError: false, FilePath: path);
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error editing file: {ex.Message}", IsError: true, FilePath: path);
        }
    }

    /// <summary>Count non-overlapping occurrences of a string.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int pos = 0;
        while ((pos = haystack.IndexOf(needle, pos, StringComparison.Ordinal)) >= 0)
        {
            count++;
            pos += needle.Length;
        }
        return count;
    }

    /// <summary>Replace the first occurrence of oldString with newString.</summary>
    private static string ReplaceFirst(string text, string oldString, string newString)
    {
        var pos = text.IndexOf(oldString, StringComparison.Ordinal);
        return pos < 0 ? text : string.Concat(text.AsSpan(0, pos), newString, text.AsSpan(pos + oldString.Length));
    }

    /// <summary>Search file contents with grep/ripgrep. Limited to 200 results.</summary>
    private async Task<ToolResult> SearchAsync(JsonElement args)
    {
        var pattern = args.GetProperty("pattern").GetString()!;
        var searchPath = args.TryGetProperty("path", out var sp) ? sp.GetString() : null;
        var fileType = args.TryGetProperty("file_type", out var ft) ? ft.GetString() : null;
        bool useRg = IsRipgrepAvailable();

        // Search allows paths outside working dir
        var resolvedSearchPath = searchPath != null
            ? ResolvePath(searchPath, allowEscape: true)
            : _workingDir;

        string command;
        if (useRg)
        {
            command = fileType != null
                ? $"rg -n --max-count 200 -g '*.{ShellExecutor.EscapeShellArg(fileType)}' -- {ShellExecutor.EscapeShellArg(pattern)} {ShellExecutor.EscapeShellArg(resolvedSearchPath)}"
                : $"rg -n --max-count 200 -- {ShellExecutor.EscapeShellArg(pattern)} {ShellExecutor.EscapeShellArg(resolvedSearchPath)}";
        }
        else
        {
            command = fileType != null
                ? $"grep -rn --include='*.{ShellExecutor.EscapeShellArg(fileType)}' -- {ShellExecutor.EscapeShellArg(pattern)} {ShellExecutor.EscapeShellArg(resolvedSearchPath)} 2>/dev/null | head -200"
                : $"grep -rn -- {ShellExecutor.EscapeShellArg(pattern)} {ShellExecutor.EscapeShellArg(resolvedSearchPath)} 2>/dev/null | head -200";
        }

        var result = await ShellExecutor.RunViaBashCAsync(command, _workingDir, 30000);

        if (result.TimedOut)
            return new ToolResult(result.Output, IsError: true);
        if (string.IsNullOrWhiteSpace(result.Output))
            return new ToolResult("No matches found.", IsError: false);
        return new ToolResult(result.Output, IsError: false);
    }

    /// <summary>
    /// Delegate a task to a sub-agent running in tmux.
    /// Uses SpawnManager if available, otherwise falls back to SpawnHandler delegate.
    /// Returns an error if neither is configured.
    /// </summary>
    private async Task<ToolResult> Spawn(JsonElement args)
    {
        var task = args.GetProperty("task").GetString()!;
        var type = args.TryGetProperty("type", out var t) ? t.GetString() ?? "small" : "small";

        // Prefer SpawnManager (core implementation) over SpawnHandler (legacy delegate)
        if (SpawnManager != null)
            return await SpawnManager.SpawnAsync(task, type);

        // Fall back to SpawnHandler delegate (TUI-wired)
        if (SpawnHandler != null)
            return await SpawnHandler(task, type);

        return new ToolResult(
            "Sub-agents not configured. Spawn requires either SpawnManager or SpawnHandler to be set.",
            IsError: true);
    }

    // --- Helpers ---

    private static readonly Lazy<bool> _isRipgrepAvailable = new(() =>
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "which", Arguments = "rg",
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            });
            p?.WaitForExit(2000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    });

    private static bool IsRipgrepAvailable() => _isRipgrepAvailable.Value;

    /// <summary>Format a ShellResult into a ToolResult, combining stdout/stderr.</summary>
    private static ToolResult FormatShellResult(ShellResult result)
    {
        var output = new StringBuilder();
        if (!string.IsNullOrEmpty(result.Output))
            output.Append(result.Output);
        if (!string.IsNullOrEmpty(result.Stderr))
        {
            if (output.Length > 0) output.AppendLine();
            output.Append($"STDERR:\n{result.Stderr}");
        }
        output.AppendLine($"\nExit code: {result.ExitCode}");
        bool isError = result.TimedOut || result.ExitCode >= 128;
        return new ToolResult(output.ToString(), IsError: isError);
    }

    /// <summary>
    /// Resolve path relative to working directory.
    /// Expands ~ to user's home directory.
    /// When allowEscape is false (write), blocks paths outside working dir.
    /// When allowEscape is true (read, search), allows any path.
    /// </summary>
    private string ResolvePath(string inputPath, bool allowEscape = false)
    {
        // Expand tilde to home directory
        string expanded = inputPath;
        if (expanded.StartsWith("~/") || expanded == "~")
        {
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            expanded = expanded == "~"
                ? homeDir
                : Path.Combine(homeDir, expanded[2..]);
        }

        // If expanded path is already absolute (e.g., after tilde expansion), use it directly
        // Otherwise combine with working directory
        var fullPath = Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(_workingDir, expanded));

        // Check if path is within ~/.little_helper/ (skills, config, logs are legitimate targets)
        var homeDir2 = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var littleHelperDir = Path.Combine(homeDir2, ".little_helper");
        var isLittleHelperPath = fullPath == littleHelperDir ||
            fullPath.StartsWith(littleHelperDir + Path.DirectorySeparatorChar);

        // Allow if: yolo mode enabled, or path is in .little_helper, or path is in working dir
        var effectiveAllowEscape = allowEscape || _allowEscape;
        if (!effectiveAllowEscape && !isLittleHelperPath && fullPath != _workingDir &&
            !fullPath.StartsWith(_workingDir + Path.DirectorySeparatorChar))
        {
            throw new InvalidOperationException(
                $"Path escape blocked: '{inputPath}' resolves outside working directory. " +
                "Enable :yolo mode to allow writing outside the project directory.");
        }

        return fullPath;
    }

    /// <summary>
    /// Display path — shows ~ for home, relative for working dir, absolute otherwise.
    /// </summary>
    private string GetDisplayPath(string fullPath)
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (fullPath.StartsWith(homeDir + Path.DirectorySeparatorChar))
            return "~" + fullPath[homeDir.Length..];
        return Path.GetRelativePath(_workingDir, fullPath);
    }

    private bool IsDestructive(string command)
    {
        var firstWord = command.Trim().Split(' ', 2)[0];
        return _destructiveCommands.Contains(firstWord);
    }
}