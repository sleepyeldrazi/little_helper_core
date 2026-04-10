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

    public ToolExecutor(string workingDirectory, bool blockDestructive = false)
    {
        _workingDir = Path.GetFullPath(workingDirectory);
        _blockDestructive = blockDestructive;

        _destructiveCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "rm", "rmdir", "del", "format", "mkfs", "dd",
            "shutdown", "reboot", "poweroff",
            "chmod", "chown", "systemctl", "service",
            "apt", "yum", "dnf", "pip",
            "sudo", "doas"
        };
    }

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
            "search" => await SearchAsync(arguments),
            "bash" => await Run(arguments),  // bash is alias for run
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
            "run" or "bash" => ValidateRun(args),
            "search" => ValidateSearch(args),
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

        var fullPath = Path.GetFullPath(Path.Combine(_workingDir, expanded));

        if (!allowEscape && fullPath != _workingDir &&
            !fullPath.StartsWith(_workingDir + Path.DirectorySeparatorChar))
        {
            throw new InvalidOperationException(
                $"Path escape blocked: '{inputPath}' resolves outside working directory. " +
                "Use bash for operations outside the project directory.");
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