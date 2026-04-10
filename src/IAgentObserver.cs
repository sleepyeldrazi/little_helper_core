using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// Observer interface for Agent FSM events. The TUI implements this to get
/// real-time updates without Agent being coupled to any UI framework.
/// All methods are optional (default no-op) — implement only what you need.
/// </summary>
public interface IAgentObserver
{
    /// <summary>Called before each FSM cycle (step number starts at 1).</summary>
    void OnStepStart(int step) { }

    /// <summary>Called after the model responds, with full response including thinking.</summary>
    void OnModelResponse(ModelResponse response, int step) { }

    /// <summary>Called before a tool is executed. Return null to skip, return modified call to edit.</summary>
    ToolCall? OnToolCallExecuting(ToolCall call, int step) => call;

    /// <summary>Called after a tool completes, with timing.</summary>
    void OnToolCallCompleted(ToolCall call, ToolResult result, long durationMs, int step) { }

    /// <summary>Called on FSM state transitions.</summary>
    void OnStateChange(AgentState from, AgentState to) { }

    /// <summary>Called when context is compacted.</summary>
    void OnCompaction(CompactionResult result) { }

    /// <summary>Called on errors and warnings.</summary>
    void OnError(string message) { }

    /// <summary>Called with streaming content as it arrives. Chunks may be empty.</summary>
    void OnStreamChunk(string contentDelta, string? thinkingDelta) { }
}

/// <summary>
/// A no-op observer that does nothing. Use as a base class or for CLI/headless mode.
/// </summary>
public class NullAgentObserver : IAgentObserver
{
    // All methods have default implementations via the interface
}