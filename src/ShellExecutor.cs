using System.Diagnostics;
using System.Text;

namespace LittleHelper;

/// <summary>
/// Shell command execution helpers. Extracted from Tools.cs to keep files under 300 lines (Rule #8).
/// Uses stdin pipe for Run/Bash commands (avoids shell injection), bash -c for Search.
/// </summary>
static class ShellExecutor
{
    /// <summary>
    /// Execute a command by piping it to bash via stdin.
    /// This avoids shell injection from string interpolation in -c arguments.
    /// </summary>
    public static async Task<ShellResult> RunViaStdinAsync(string command, string workingDir, int timeoutSec)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = "-s",  // read commands from stdin
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            await process.StandardInput.WriteAsync(command);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var completed = process.WaitForExit(timeoutSec * 1000);
            if (!completed)
            {
                process.Kill(entireProcessTree: true);
                var partialOut = await stdoutTask;
                return new ShellResult(
                    $"Command timed out after {timeoutSec}s.\nPartial output:\n{partialOut}",
                    ExitCode: -1, TimedOut: true);
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ShellResult(stdout, process.ExitCode, TimedOut: false, Stderr: stderr);
        }
        catch (Exception ex)
        {
            return new ShellResult($"Error running command: {ex.Message}", ExitCode: -1, TimedOut: false);
        }
    }

    /// <summary>
    /// Execute a command via bash -c (for search commands where the command is constructed internally).
    /// The command is built from escaped arguments, not arbitrary model input.
    /// </summary>
    public static async Task<ShellResult> RunViaBashCAsync(string command, string workingDir, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{command.Replace("\"", "\\\\\"")}\"",
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var completed = process.WaitForExit(timeoutMs);
            if (!completed)
            {
                process.Kill(entireProcessTree: true);
                var partialOut = await stdoutTask;
                return new ShellResult(
                    $"Search timed out.\nPartial output:\n{partialOut}",
                    ExitCode: -1, TimedOut: true);
            }

            var stdout = await stdoutTask;
            return new ShellResult(stdout, process.ExitCode, TimedOut: false);
        }
        catch (Exception ex)
        {
            return new ShellResult($"Error: {ex.Message}", ExitCode: -1, TimedOut: false);
        }
    }

    /// <summary>Escape a string for safe use as a shell argument (single-quoted).</summary>
    public static string EscapeShellArg(string arg)
    {
        return $"'{arg.Replace("'", "'\\''")}'";
    }
}

/// <summary>Result of a shell command execution.</summary>
record ShellResult(string Output, int ExitCode, bool TimedOut, string? Stderr = null);