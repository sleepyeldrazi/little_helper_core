using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// Core agent FSM loop. Single state machine: call model -> execute tools -> observe -> done.
/// Research-backed: explicit FSM (63.73% vs 40.3% for ReAct), minimal system prompt, stall detection.
/// </summary>
public class Agent
{
    private readonly AgentConfig _config;
    private readonly IModelClient _modelClient;
    private readonly ToolExecutor _toolExecutor;
    private readonly SkillDiscovery _skills;
    private readonly Compaction _compactor;
    private readonly PromptBuilder _promptBuilder;
    private readonly SessionLogger? _logger;
    private readonly IAgentObserver? _observer;
    private readonly AgentControl _control;
    private List<string> _filesChanged;

    // Stall detection: circular buffer of recent observations
    private string?[] _recentObservations;
    private int _observationIndex;

    // Conversation history: accessible for TUI (read-only from outside)
    private List<ChatMessage> _messages = new();

    /// <summary>Read-only access to conversation history for TUI.</summary>
    public IReadOnlyList<ChatMessage> History => _messages.AsReadOnly();

    /// <summary>Clear conversation history. Call before RunAsync to start fresh.</summary>
    public void ClearHistory() => _messages = new List<ChatMessage>();

    /// <summary>Access to the control surface (pause/resume/inject/intercept).</summary>
    public AgentControl Control => _control;

    public Agent(AgentConfig config, IModelClient modelClient, ToolExecutor toolExecutor,
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
        _control = new AgentControl();
        _filesChanged = new List<string>();
        _recentObservations = new string?[config.StallThreshold];
        _observationIndex = 0;
    }

    /// <summary>
    /// Run the agent loop until done or step limit reached.
    /// When clearHistory is false, appends the user prompt to existing conversation
    /// history, enabling multi-turn conversation.
    /// </summary>
    public async Task<AgentResult> RunAsync(string userPrompt, CancellationToken ct = default,
        bool clearHistory = true)
    {
        var state = AgentState.Planning;
        if (clearHistory)
        {
            _messages = new List<ChatMessage>();
        }
        int step = 0;
        int errorRecoveryCount = 0;
        string finalOutput = "";

        // Reset per-run state (safe for reuse)
        _filesChanged = new List<string>();
        Array.Clear(_recentObservations, 0, _recentObservations.Length);
        _observationIndex = 0;

        while (state != AgentState.Done && step < _config.MaxSteps)
        {
            ct.ThrowIfCancellationRequested();

            // Pause checkpoint: block here if paused, wake on Resume()
            _control.WaitIfPaused(ct);

            // Drain any injected messages
            foreach (var inj in _control.DrainInjectedMessages())
            {
                _messages.Add(ChatMessage.User(inj));
                _observer?.OnError($"[Injected] {inj[..Math.Min(80, inj.Length)]}");
            }

            switch (state)
            {
                case AgentState.Planning:
                    if (clearHistory || _messages.Count == 0)
                    {
                        Log("[State] Planning — building initial context");
                        _messages = _promptBuilder.BuildInitialContext(userPrompt);
                    }
                    else
                    {
                        Log("[State] Planning — continuing conversation");
                        _messages.Add(ChatMessage.User(userPrompt));
                    }
                    state = TransitionState(state, AgentState.Executing);
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
                    var response = await _modelClient.Complete(_messages, ct, observer: _observer, enableStreaming: _config.EnableStreaming);
                    step++;
                    Log($"[Step {step}] Model responded ({response.TokensUsed} tokens, {response.ToolCalls.Count} tool calls)");
                    if (_logger == null) Log("[Warning] SessionLogger is null, step will not be logged");
                    _logger?.Step(step, response.TokensUsed, response.ThinkingTokens,
                        response.ToolCalls.Count, response.ThinkingContent, response.Content);

                    _observer?.OnModelResponse(response, step);
                    _messages.Add(ChatMessage.Assistant(response.Content, response.ToolCalls, response.ThinkingContent));

                    if (response.ToolCalls.Count == 0)
                    {
                        finalOutput = response.Content;
                        state = TransitionState(state, AgentState.Done);
                    }
                    else
                    {
                        state = TransitionState(state, AgentState.Observing);
                    }
                    break;

                case AgentState.Observing:
                    Log("[State] Observing — executing tool calls");
                    bool hadError = false;

                    var lastAssistant = _messages.LastOrDefault(m => m.Role == "assistant" && m.ToolCalls?.Count > 0);
                    var toolCalls = lastAssistant?.ToolCalls ?? new List<ToolCall>();

                    foreach (var toolCall in toolCalls)
                    {
                        // Tool interception: allow TUI to skip/edit calls
                        var intercepted = _observer?.OnToolCallExecuting(toolCall, step) ?? toolCall;
                        var interceptorResult = _control.ToolInterceptor?.Invoke(toolCall);
                        if (interceptorResult != null) intercepted = interceptorResult;

                        if (intercepted == null)
                        {
                            Log($"  {toolCall.Name} SKIPPED (intercepted)");
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
                        state = TransitionState(state, AgentState.Done);
                        break;
                    }

                    if (hadError)
                    {
                        if (errorRecoveryCount < _config.MaxRetries)
                        {
                            errorRecoveryCount++;
                            Log($"[State] Error recovery (attempt {errorRecoveryCount}/{_config.MaxRetries})");
                            state = TransitionState(state, AgentState.ErrorRecovery);
                        }
                        else
                        {
                            Log($"[State] Max error recovery attempts reached ({errorRecoveryCount}/{_config.MaxRetries}), forcing done");
                            finalOutput = "Max error recovery attempts reached. Stopping.";
                            state = TransitionState(state, AgentState.Done);
                        }
                    }
                    else
                    {
                        errorRecoveryCount = 0;
                        state = TransitionState(state, AgentState.Executing);
                    }
                    break;

                case AgentState.ErrorRecovery:
                    var recentErrors = _messages
                        .Where(m => m.Role == "tool" && m.ToolResult?.IsError == true)
                        .TakeLast(1)
                        .Select(m => m.Content)
                        .FirstOrDefault() ?? "";
                    var errorHint = string.IsNullOrEmpty(recentErrors)
                        ? "The previous tool call failed. Consider an alternative approach."
                        : $"The previous tool call failed: {recentErrors[..Math.Min(300, recentErrors.Length)]}\nConsider an alternative approach or fix the error before retrying.";
                    _messages.Add(ChatMessage.User(errorHint));
                    state = TransitionState(state, AgentState.Executing);
                    break;
            }
        }

        if (step >= _config.MaxSteps && state != AgentState.Done)
        {
            Log($"[State] Step limit reached ({step}/{_config.MaxSteps})");
            finalOutput = "Step limit reached. Stopping.";
        }

        Log($"[Done] Steps: {step}, Tokens: {_modelClient.TotalTokensUsed}, Thinking: {_modelClient.TotalThinkingTokens}, Success: {state == AgentState.Done}");

        if (_logger == null) Log("[Warning] SessionLogger is null at session end");
        _logger?.End(state == AgentState.Done, step, _modelClient.TotalTokensUsed,
            _modelClient.TotalThinkingTokens, _filesChanged);

        return new AgentResult(
            Success: state == AgentState.Done,
            Output: finalOutput,
            FilesChanged: _filesChanged,
            ThinkingLog: _modelClient.ThinkingLog,
            TotalThinkingTokens: _modelClient.TotalThinkingTokens);
    }

    /// <summary>Transition state and notify observer.</summary>
    private AgentState TransitionState(AgentState from, AgentState to)
    {
        _observer?.OnStateChange(from, to);
        return to;
    }

    /// <summary>Format the last observation for stall detection.</summary>
    private string FormatLastObservation(List<ChatMessage> messages)
    {
        return string.Join("\n", messages
            .Where(m => m.Role == "tool" && m.Content != null)
            .TakeLast(3)
            .Select(m => m.Content!));
    }

    /// <summary>
    /// Check if observations are stalling (repeated same content).
    /// Research: threshold of 5 (models need 3-4 reads before editing).
    /// </summary>
    private bool IsStalled(string observation)
    {
        if (string.IsNullOrWhiteSpace(observation))
            return false;

        var normalized = string.Join(" ", observation.Split(new[] { ' ', '\n', '\r', '\t' },
            StringSplitOptions.RemoveEmptyEntries));

        _recentObservations[_observationIndex] = normalized;
        _observationIndex = (_observationIndex + 1) % _recentObservations.Length;

        var nonNull = _recentObservations.Where(o => o != null).ToList();
        if (nonNull.Count < _config.StallThreshold)
            return false;

        var first = nonNull[0]!;
        return nonNull.All(o => o == first);
    }

    /// <summary>Log a message via observer (or stderr fallback).</summary>
    private void Log(string message)
    {
        if (_observer != null)
            _observer.OnError(message);
        else
            Console.Error.WriteLine(message);
    }

    /// <summary>
    /// Format tool arguments into a short human-readable summary for logging.
    /// read -> path, write -> path, run/bash -> command, search -> pattern.
    /// Public so TUI and other consumers can reuse the formatting.
    /// </summary>
    public static string FormatToolDetail(string toolName, System.Text.Json.JsonElement args)
    {
        try
        {
            return toolName.ToLowerInvariant() switch
            {
                "read" => args.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "",
                "write" => args.TryGetProperty("path", out var w) ? w.GetString() ?? "" : "",
                "edit" or "patch" => args.TryGetProperty("path", out var e) ? e.GetString() ?? "" : "",
                "run" or "bash" => args.TryGetProperty("command", out var c) ? Truncate(c.GetString() ?? "", 80) : "",
                "search" => args.TryGetProperty("pattern", out var s) ? $"\"{s.GetString()}\"" : "",
                "spawn" => args.TryGetProperty("task", out var sp)
                    ? Truncate(sp.GetString() ?? "", 60) : "",
                _ => args.GetRawText()[..Math.Min(60, args.GetRawText().Length)]
            };
        }
        catch { return ""; }
    }

    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "...";
}