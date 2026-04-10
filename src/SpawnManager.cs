using System.Text;
using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// Manages sub-agent spawning in tmux sessions.
/// Two tiers: "small" (quick task, concise summary) and "complex" (plan, execute, report).
/// Tracks all spawns in a JSONL log that survives context compaction.
/// </summary>
public class SpawnManager
{
    private readonly string _socketPath;
    private readonly string _sessionName;
    private readonly string _logFilePath;
    private readonly string _binaryPath;
    private readonly int _maxLogEntries;

    // Tier-specific system prompts injected before the task
    private const string SmallTierPrompt = """
        You are a sub-agent performing a focused task. Complete the task, then provide a SHORT, EXHAUSTIVE, and CONCISE summary of your findings or results. Do not include step-by-step narration — just the final answer. Be precise and factual.
        """;

    private const string ComplexTierPrompt = """
        You are a sub-agent performing a complex task. Before starting:
        1. Analyze the task and write out a brief plan/strategy.
        2. List a rough TODO of steps.
        3. Execute the plan, adapting as needed.
        4. At the end, report: what you did, what worked, what didn't, and any remaining issues.
        """;

    public SpawnManager(string? binaryPath = null, string? logDir = null)
    {
        _socketPath = "/tmp/tmux-lh-spawn";
        _sessionName = "lh-spawn";
        _logFilePath = Path.Combine(logDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".little_helper"), "spawn-log.jsonl");
        _binaryPath = binaryPath ?? "little";
        _maxLogEntries = 50;
    }

    /// <summary>Spawn a sub-agent in a new tmux window. Returns window name and interaction instructions.</summary>
    public async Task<ToolResult> SpawnAsync(string task, string type)
    {
        // Ensure tmux is available
        if (!await IsTmuxAvailableAsync())
            return new ToolResult("Spawn requires tmux. Install with: sudo apt install tmux", IsError: true);

        var tier = type == "complex" ? "complex" : "small";
        var windowName = GenerateWindowName(tier);

        // Build the tier-specific prompt + task
        var tierPrompt = tier == "complex" ? ComplexTierPrompt : SmallTierPrompt;
        var fullPrompt = $"{tierPrompt}\n\nTASK:\n{task}";

        // Escape for tmux send-keys (single-quote escaping)
        var escapedPrompt = EscapeForTmux(fullPrompt);
        var escapedCommand = EscapeForTmux(_binaryPath);

        // Create session (idempotent) + window + start agent + send task
        var spawnCmd = new[]
        {
            $"tmux -S {_socketPath} new-session -d -s {_sessionName} 2>/dev/null || true",
            $"tmux -S {_socketPath} new-window -d -a -n {windowName} -t {_sessionName}",
            $"tmux -S {_socketPath} send-keys -t '{_sessionName}:{windowName}' {escapedCommand} C-m",
            "sleep 2",
            $"tmux -S {_socketPath} send-keys -t '{_sessionName}:{windowName}' {escapedPrompt} C-m",
        };

        var cmd = string.Join(" && ", spawnCmd);

        var result = await ShellExecutor.RunViaBashCAsync(cmd,
            Directory.GetCurrentDirectory(), 30000);

        if (result.ExitCode != 0 && result.ExitCode != -1)
        {
            // Exit code from new-session failing is fine (session already exists)
            // but other failures are real
            if (!result.Output.Contains("session already exists") &&
                !string.IsNullOrEmpty(result.Output.Trim()))
            {
                return new ToolResult($"Failed to spawn: {result.Output}", IsError: true);
            }
        }

        // Wait for agent to start and receive the prompt
        await Task.Delay(2000);

        // Verify window exists
        var verifyCmd = $"tmux -S {_socketPath} list-windows -t {_sessionName} -F '#{{window_name}}' 2>/dev/null | grep -q '^{windowName}$'";
        var verifyResult = await ShellExecutor.RunViaBashCAsync(verifyCmd,
            Directory.GetCurrentDirectory(), 5000);

        if (verifyResult.ExitCode != 0)
        {
            return new ToolResult("Agent window not found after spawn. Check tmux session.", IsError: true);
        }

        // Log the spawn
        AppendLog(new SpawnLogEntry(
            Timestamp: DateTime.UtcNow.ToString("O"),
            WindowName: windowName,
            Task: task,
            Tier: tier,
            Status: "created"
        ));

        // Capture initial output
        var initialOutput = await CaptureWindowAsync(windowName);

        var info = new StringBuilder();
        info.AppendLine($"Spawned {tier} agent: {windowName}");
        info.AppendLine();
        info.AppendLine("Interact with the agent:");
        info.AppendLine($"  Capture output: tmux -S {_socketPath} capture-pane -t '{_sessionName}:{windowName}' -J -p");
        info.AppendLine($"  Send input:     tmux -S {_socketPath} send-keys -t '{_sessionName}:{windowName}' 'your message' C-m");
        info.AppendLine($"  List windows:   tmux -S {_socketPath} list-windows -t {_sessionName}");
        info.AppendLine($"  Kill window:    tmux -S {_socketPath} kill-window -t '{_sessionName}:{windowName}'");
        info.AppendLine();
        info.AppendLine("--- Initial output ---");
        info.AppendLine(string.IsNullOrEmpty(initialOutput) ? "(starting...)" : initialOutput);

        return new ToolResult(info.ToString(), IsError: false);
    }

    /// <summary>Capture the current output of a spawn window.</summary>
    public async Task<string> CaptureWindowAsync(string windowName)
    {
        try
        {
            var cmd = $"tmux -S {_socketPath} capture-pane -t '{_sessionName}:{windowName}' -J -p 2>/dev/null | tail -40";
            var result = await ShellExecutor.RunViaBashCAsync(cmd,
                Directory.GetCurrentDirectory(), 5000);
            return result.Output.Trim();
        }
        catch
        {
            return "(failed to capture)";
        }
    }

    /// <summary>Send input to a spawn window.</summary>
    public async Task<ToolResult> SendToWindowAsync(string windowName, string message)
    {
        var escaped = EscapeForTmux(message);
        var cmd = $"tmux -S {_socketPath} send-keys -t '{_sessionName}:{windowName}' {escaped} C-m";
        var result = await ShellExecutor.RunViaBashCAsync(cmd,
            Directory.GetCurrentDirectory(), 5000);

        if (result.ExitCode != 0)
            return new ToolResult($"Failed to send to {windowName}: {result.Output}", IsError: true);

        return new ToolResult($"Sent to {windowName}", IsError: false);
    }

    /// <summary>Kill a spawn window and update log status.</summary>
    public async Task<ToolResult> KillWindowAsync(string windowName)
    {
        var cmd = $"tmux -S {_socketPath} kill-window -t '{_sessionName}:{windowName}' 2>/dev/null";
        await ShellExecutor.RunViaBashCAsync(cmd, Directory.GetCurrentDirectory(), 5000);

        UpdateLogStatus(windowName, "killed");
        return new ToolResult($"Killed {windowName}", IsError: false);
    }

    /// <summary>List all active spawn windows.</summary>
    public async Task<ToolResult> ListWindowsAsync()
    {
        var cmd = $"tmux -S {_socketPath} list-windows -t {_sessionName} -F '#I: #W' 2>/dev/null";
        var result = await ShellExecutor.RunViaBashCAsync(cmd,
            Directory.GetCurrentDirectory(), 5000);

        if (result.ExitCode != 0)
            return new ToolResult("No active spawn sessions.", IsError: false);

        return new ToolResult(result.Output.Trim(), IsError: false);
    }

    /// <summary>View recent spawn log entries.</summary>
    public ToolResult ViewLog(int count = 10)
    {
        var entries = ReadLogEntries().TakeLast(count).Reverse().ToList();

        if (entries.Count == 0)
            return new ToolResult("No spawns in log.", IsError: false);

        var sb = new StringBuilder();
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var icon = e.Status switch
            {
                "created" => "*",
                "killed" => "X",
                "exited" => "o",
                _ => "?"
            };
            var time = DateTime.TryParse(e.Timestamp, out var dt)
                ? dt.ToLocalTime().ToString("HH:mm:ss") : e.Timestamp[..Math.Min(8, e.Timestamp.Length)];
            var preview = e.Task.Length > 50 ? e.Task[..50] + "..." : e.Task;
            sb.AppendLine($"{i + 1}. [{icon}] {e.WindowName} ({e.Tier})");
            sb.AppendLine($"   {time}: {preview}");
        }

        return new ToolResult(sb.ToString().Trim(), IsError: false);
    }

    // --- Private helpers ---

    private string GenerateWindowName(string tier)
    {
        var random = Random.Shared.Next(100000, 999999);
        return $"spawn-{tier}-{random}";
    }

    private static string EscapeForTmux(string input)
    {
        // Single-quote escape for tmux send-keys
        return $"'{input.Replace("'", "'\\''")}'";
    }

    private async Task<bool> IsTmuxAvailableAsync()
    {
        try
        {
            var result = await ShellExecutor.RunViaBashCAsync("which tmux",
                Directory.GetCurrentDirectory(), 3000);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // --- Spawn Log ---

    private void AppendLog(SpawnLogEntry entry)
    {
        try
        {
            var dir = Path.GetDirectoryName(_logFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var line = JsonSerializer.Serialize(entry) + "\n";
            File.AppendAllText(_logFilePath, line);

            TrimLogFile();
        }
        catch
        {
            // Log failures are non-fatal
        }
    }

    private void UpdateLogStatus(string windowName, string status)
    {
        try
        {
            if (!File.Exists(_logFilePath)) return;

            var lines = File.ReadAllLines(_logFilePath)
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            var entries = lines
                .Select(l => JsonSerializer.Deserialize<SpawnLogEntry>(l))
                .Where(e => e != null)
                .ToList();

            // Update last matching entry
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i]!.WindowName == windowName)
                {
                    entries[i] = entries[i]! with { Status = status };
                    break;
                }
            }

            File.WriteAllLines(_logFilePath,
                entries.Select(e => JsonSerializer.Serialize(e)));
        }
        catch
        {
            // Log update failures are non-fatal
        }
    }

    private List<SpawnLogEntry> ReadLogEntries()
    {
        try
        {
            if (!File.Exists(_logFilePath)) return new();

            return File.ReadAllLines(_logFilePath)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => JsonSerializer.Deserialize<SpawnLogEntry>(l))
                .Where(e => e != null)
                .ToList()!;
        }
        catch
        {
            return new();
        }
    }

    private void TrimLogFile()
    {
        try
        {
            if (!File.Exists(_logFilePath)) return;

            var lines = File.ReadAllLines(_logFilePath)
                .Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

            if (lines.Count > _maxLogEntries)
            {
                var trimmed = lines.TakeLast(_maxLogEntries);
                File.WriteAllLines(_logFilePath, trimmed);
            }
        }
        catch
        {
            // Trim failures are non-fatal
        }
    }
}

/// <summary>A spawn log entry persisted to JSONL.</summary>
public record SpawnLogEntry(
    string Timestamp,
    string WindowName,
    string Task,
    string Tier,
    string Status
);
