using System.Collections.Concurrent;

namespace LittleHelper;

/// <summary>
/// Thread-safe control surface for an Agent. Extracted from Agent.cs (Rule #8: files ≤ 300 lines).
/// The TUI uses these methods to pause, resume, inject messages, and intercept tool calls.
/// </summary>
public class AgentControl
{
    private readonly ManualResetEventSlim _pauseGate = new(true);
    private readonly ConcurrentQueue<string> _injectedMessages = new();

    /// <summary>Whether the agent is currently paused between steps.</summary>
    public bool IsPaused => !_pauseGate.IsSet;

    /// <summary>
    /// Tool call interceptor. Called before each tool execution.
    /// Return null to skip the call, return a modified ToolCall to edit arguments.
    /// </summary>
    public Func<ToolCall, ToolCall?>? ToolInterceptor { get; set; }

    /// <summary>Pause the agent. It will stop at the next step boundary.</summary>
    public void Pause() => _pauseGate.Reset();

    /// <summary>Resume the agent after a pause.</summary>
    public void Resume() => _pauseGate.Set();

    /// <summary>
    /// Wait for the pause gate to open. Called between agent steps.
    /// Throws OperationCanceledException if cancellation is requested.
    /// </summary>
    public void WaitIfPaused(CancellationToken ct = default) => _pauseGate.Wait(ct);

    /// <summary>Inject a user message to be processed at the next step boundary.</summary>
    public void InjectMessage(string message) => _injectedMessages.Enqueue(message);

    /// <summary>Dequeue all injected messages. Called between agent steps.</summary>
    public IEnumerable<string> DrainInjectedMessages()
    {
        var messages = new List<string>();
        while (_injectedMessages.TryDequeue(out var msg))
            messages.Add(msg);
        return messages;
    }
}