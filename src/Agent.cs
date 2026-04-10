using System.Text.Json;
using System.Collections.Concurrent;

namespace LittleHelper;

/// <summary>
/// Core agent FSM loop. Single state machine: call model -> execute tools -> observe -> done.
/// Research-backed: explicit FSM (63.73% vs 40.3% for ReAct), minimal system prompt, stall detection.
/// </summary>
public class Agent
{
    private readonly AgentConfig _config;
    private readonly ModelClient _modelClient;
    private readonly ToolExecutor _toolExecutor;
    private readonly SkillDiscovery _skills;
    private readonly Compaction _compactor;
    private readonly PromptBuilder _promptBuilder;
    private readonly SessionLogger? _logger;
    private readonly IAgentObserver? _observer;
    private List<string> _filesChanged;

    // Stall detection: circular buffer of recent observations
    private string?[] _recentObservations;
    private int _observationIndex;

    // Pause/resume: checked between FSM steps
    private readonly ManualResetEventSlim _pauseGate = new(true);

    // Message injection: thread-safe queue drained between steps
    private readonly ConcurrentQueue<string> _injectedMessages = new();

    // Conversation history: accessible for TUI (read-only from outside)
    private List<ChatMessage> _messages = new();

    /// <summary>Read-only access to conversation history for TUI.</summary>
    public IReadOnlyList<ChatMessage> History => _messages.AsReadOnly();

    /// <summary>Whether the agent is currently paused between steps.</summary>
    public bool IsPaused => !_pauseGate.IsSet;

    public Agent(AgentConfig config, ModelClient modelClient, ToolExecutor toolExecutor,
        SkillDiscovery skills, SessionLogger? logger = null, IAgentObserver? observer = null)
    {
        _config = config;
        _modelClient = modelClient;
        _toolExecutor = toolExecutor;
        _skills = skills;
        _compactor = new Compaction(config);
        _promptBuilder = new PromptBuilder(config, skills);
        _logger = logger;
        _observer = observer;
        _filesChanged = new List<string>();
        _recentObservations = new string?[config.StallThreshold];
        _observationIndex = 0;
    }

    /// <summary>Pause the agent. It will stop at the next step boundary.</summary>
    public void Pause() => _pauseGate.Reset();

    /// <summary>Resume the agent after a pause.</summary>
    public void Resume() => _pauseGate.Set();

    /// <summary>Inject a user message to be processed at the next step boundary.</summary>
    public void InjectMessage(string message) => _injectedMessages.Enqueue(message);

    /// <summary>
    /// Set a tool call interceptor. Called before each tool execution.
    /// Return null to skip the call, return a modified ToolCall to edit arguments.
    /// </summary>
    public Func<ToolCall, ToolCall?>? ToolInterceptor { get; set; }

    /// <summary>
    /// Run the agent loop until done or step limit reached.
    /// </summary>
    public async Task<AgentResult> RunAsync(string userPrompt, CancellationToken ct = default)
    {
        var state = AgentState.Planning;
        _messages = new List<ChatMessage>();
        int step = 0;
        int errorRecoveryCount = 0;
        string finalOutput = "";

        // Reset state for each run (safe for reuse)
        _filesChanged = new List<string>();
        Array.Clear(_recentObservations, 0, _recentObservations.Length);
        _observationIndex = 0;

        while (state != AgentState.Done && step < _config.MaxSteps)
        {
            ct.ThrowIfCancellationRequested();

            // Pause checkpoint: block here if paused, wake on Resume()
            _pauseGate.Wait(ct);

            // Drain any injected messages
            while (_injectedMessages.TryDequeue(out var injected))
            {
                _messages.Add(ChatMessage.User(injected));
                _observer?.OnError($"[Injected] {injected[..Math.Min(80, injected.Length)]}");
            }

            switch (state)
            {
                case AgentState.Planning:
                    Log("[State] Planning — building initial context");
                    _messages = _promptBuilder.BuildInitialContext(userPrompt);
                    var fromPlanning = state;
                    state = AgentState.Executing;
                    _observer?.OnStateChange(fromPlanning, state);
                    break;

                case AgentState.Executing:
                    // Compact context if approaching token limit
                    if (_compactor.NeedsCompaction(_messages))
                    {
                        var compacted = _compactor.CompactIfNeeded(_messages);
                        _messages = compacted.Messages;
                        _observer?.OnCompaction(compacted);
                    }

                    _observer?.OnStepStart(step + 1);
                    var response = await _modelClient.Complete(_messages, ct, observer: _observer);
                    step++;
                    Log($"[Step {step}] Model responded ({response.TokensUsed} tokens, {response.ToolCalls.Count} tool calls)");
                    _logger?.Step(step, response.TokensUsed, response.ThinkingTokens,
                        response.ToolCalls.Count, response.ThinkingContent, response.Content);

                    _observer?.OnModelResponse(response, step);
                    _messages.Add(ChatMessage.Assistant(response.Content, response.ToolCalls, response.ThinkingContent));

                    if (response.ToolCalls.Count == 0)
                    {
                        finalOutput = response.Content;
                        var fromExecuting = state;
                        state = AgentState.Done;
                        _observer?.OnStateChange(fromExecuting, state);
                    }
                    else
                    {
                        var fromExecuting2 = state;
                        state = AgentState.Observing;
                        _observer?.OnStateChange(fromExecuting2, state);
                    }
                    break;

                case AgentState.Observing:
                    Log("[State] Observing — executing tool calls");
                    bool hadError = false;

                    // Get the assistant message with tool calls
                    var lastAssistant = _messages.LastOrDefault(m => m.Role == "assistant" && m.ToolCalls?.Count > 0);
                    var toolCalls = lastAssistant?.ToolCalls ?? new List<ToolCall>();

                    foreach (var toolCall in toolCalls)
                    {
                        // Tool interception: allow TUI to skip/edit calls
                        var intercepted = _observer?.OnToolCallExecuting(toolCall, step) ?? toolCall;
                        if (intercepted == null)
                        {
                            Log($"  {toolCall.Name} SKIPPED (intercepted)");
                            // Still need to send a tool result so the model doesn't hang
                            var skipResult = new ToolResult("Tool call skipped by user.", true);
                            _messages.Add(ChatMessage.FromToolResult(toolCall.Id, skipResult));
                            continue;
                        }

                        var detail = FormatToolDetail(intercepted.Name, intercepted.Arguments);
                        Log($"  {intercepted.Name} {detail}");
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var result = await _toolExecutor.Execute(intercepted.Name, intercepted.Arguments);
                        sw.Stop();
                        _messages.Add(ChatMessage.FromToolResult(intercepted.Id, result));

                        _observer?.OnToolCallCompleted(intercepted, result, sw.ElapsedMilliseconds, step);
                        _logger?.ToolCall(intercepted.Name, detail,
                            result.Output, result.IsError, result.FilePath, sw.ElapsedMilliseconds);

                        if (result.IsError)
                        {
                            hadError = true;
                            Log($"  ERROR: {result.Output[..Math.Min(200, result.Output.Length)]}");
                        }
                        else if (!string.IsNullOrEmpty(result.FilePath))
                        {
                            Log($"  -> {result.FilePath}");
                        }

                        if (!string.IsNullOrEmpty(result.FilePath) &&
                            !result.IsError &&
                            !_filesChanged.Contains(result.FilePath))
                        {
                            _filesChanged.Add(result.FilePath);
                        }
                    }

                    // Stall detection
                    var lastObservation = FormatLastObservation(_messages);
                    if (IsStalled(lastObservation))
                    {
                        Log("[State] Stall detected — stopping");
                        finalOutput = "Stall detected: repeated observations. Stopping.";
                        var fromObserving = state;
                        state = AgentState.Done;
                        _observer?.OnStateChange(fromObserving, state);
                        break;
                    }

                    if (hadError)
                    {
                        if (errorRecoveryCount < _config.MaxRetries)
                        {
                            errorRecoveryCount++;
                            Log($"[State] Error recovery (attempt {errorRecoveryCount}/{_config.MaxRetries})");
                            var fromObserving2 = state;
                            state = AgentState.ErrorRecovery;
                            _observer?.OnStateChange(fromObserving2, state);
                        }
                        else
                        {
                            Log($"[State] Max error recovery attempts reached ({errorRecoveryCount}/{_config.MaxRetries}), forcing done");
                            finalOutput = "Max error recovery attempts reached. Stopping.";
                            var fromObserving3 = state;
                            state = AgentState.Done;
                            _observer?.OnStateChange(fromObserving3, state);
                        }
                    }
                    else
                    {
                        // Successful observation resets the error recovery counter
                        errorRecoveryCount = 0;
                        var fromObserving4 = state;
                        state = AgentState.Executing;
                        _observer?.OnStateChange(fromObserving4, state);
                    }
                    break;

                case AgentState.ErrorRecovery:
                    // Inject error context as a system message
                    var recentErrors = _messages
                        .Where(m => m.Role == "tool" && m.ToolResult?.IsError == true)
                        .TakeLast(1)
                        .Select(m => m.Content)
                        .FirstOrDefault() ?? "";
                    var errorHint = string.IsNullOrEmpty(recentErrors)
                        ? "The previous tool call failed. Consider an alternative approach."
                        : $"The previous tool call failed: {recentErrors[..Math.Min(300, recentErrors.Length)]}\nConsider an alternative approach or fix the error before retrying.";
                    _messages.Add(ChatMessage.System(errorHint));
                    var fromRecovery = state;
                    state = AgentState.Executing;
                    _observer?.OnStateChange(fromRecovery, state);
                    break;
            }
        }

        if (step >= _config.MaxSteps && state != AgentState.Done)
        {
            Log($"[State] Step limit reached ({step}/{_config.MaxSteps})");
            finalOutput = "Step limit reached. Stopping.";
        }

        Log($"[Done] Steps: {step}, Tokens: {_modelClient.TotalTokensUsed}, Thinking: {_modelClient.TotalThinkingTokens}, Success: {state == AgentState.Done}");

        _logger?.End(state == AgentState.Done, step, _modelClient.TotalTokensUsed,
            _modelClient.TotalThinkingTokens, _filesChanged);

        return new AgentResult(
            Success: state == AgentState.Done,
            Output: finalOutput,
            FilesChanged: _filesChanged,
            ThinkingLog: _modelClient.ThinkingLog,
            TotalThinkingTokens: _modelClient.TotalThinkingTokens);
    }

    /// <summary>
    /// Format the last observation for stall detection.
    /// </summary>
    private string FormatLastObservation(List<ChatMessage> messages)
    {
        // Look at the last few tool results
        var toolResults = messages
            .Where(m => m.Role == "tool" && m.Content != null)
            .TakeLast(3)
            .Select(m => m.Content!)
            .ToList();

        return string.Join("\n", toolResults);
    }

    /// <summary>
    /// Check if observations are stalling (repeated same content).
    /// Research: threshold of 5 (models need 3-4 reads before editing).
    /// </summary>
    private bool IsStalled(string observation)
    {
        if (string.IsNullOrWhiteSpace(observation))
            return false;

        // Normalize: collapse whitespace
        var normalized = string.Join(" ", observation.Split(new[] { ' ', '\n', '\r', '\t' },
            StringSplitOptions.RemoveEmptyEntries));

        // Store in circular buffer
        _recentObservations[_observationIndex] = normalized;
        _observationIndex = (_observationIndex + 1) % _recentObservations.Length;

        // Check if all entries are the same (and all filled)
        var nonNull = _recentObservations.Where(o => o != null).ToList();
        if (nonNull.Count < _config.StallThreshold)
            return false;

        var first = nonNull[0]!;
        return nonNull.All(o => o == first);
    }

    /// <summary>Log a message to stderr for debugging/progress tracking.</summary>
    private static void Log(string message) => Console.Error.WriteLine(message);

    /// <summary>
    /// Format tool arguments into a short human-readable summary for logging.
    /// read -> path, write -> path, run/bash -> command, search -> pattern.
    /// </summary>
    private static string FormatToolDetail(string toolName, System.Text.Json.JsonElement args)
    {
        try
        {
            return toolName.ToLowerInvariant() switch
            {
                "read" => args.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                "write" => args.TryGetProperty("path", out var w) ? w.GetString() ?? "" : "",
                "run" or "bash" => args.TryGetProperty("command", out var c) ? Truncate(c.GetString() ?? "", 80) : "",
                "search" => args.TryGetProperty("pattern", out var s) ? $"\"{s.GetString()}\"" : "",
                _ => args.GetRawText()[..Math.Min(60, args.GetRawText().Length)]
            };
        }
        catch
        {
            return "";
        }
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";
}