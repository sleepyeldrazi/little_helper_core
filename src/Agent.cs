using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// Core agent FSM loop. Single state machine: call model -> execute tools -> observe -> done.
/// Research-backed: explicit FSM (63.73% vs 40.3% for ReAct), minimal system prompt, stall detection.
/// </summary>
class Agent
{
    private readonly AgentConfig _config;
    private readonly ModelClient _modelClient;
    private readonly ToolExecutor _toolExecutor;
    private readonly SkillDiscovery _skills;
    private readonly Compaction _compactor;
    private readonly PromptBuilder _promptBuilder;
    private readonly SessionLogger? _logger;
    private List<string> _filesChanged;

    // Stall detection: circular buffer of recent observations
    private string?[] _recentObservations;
    private int _observationIndex;

    public Agent(AgentConfig config, ModelClient modelClient, ToolExecutor toolExecutor,
        SkillDiscovery skills, SessionLogger? logger = null)
    {
        _config = config;
        _modelClient = modelClient;
        _toolExecutor = toolExecutor;
        _skills = skills;
        _compactor = new Compaction(config);
        _promptBuilder = new PromptBuilder(config, skills);
        _logger = logger;
        _filesChanged = new List<string>();
        _recentObservations = new string?[config.StallThreshold];
        _observationIndex = 0;
    }

    /// <summary>
    /// Run the agent loop until done or step limit reached.
    /// </summary>
    public async Task<AgentResult> RunAsync(string userPrompt, CancellationToken ct = default)
    {
        var state = AgentState.Planning;
        var messages = new List<ChatMessage>();
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

            switch (state)
            {
                case AgentState.Planning:
                    Log("[State] Planning — building initial context");
                    messages = _promptBuilder.BuildInitialContext(userPrompt);
                    state = AgentState.Executing;
                    break;

                case AgentState.Executing:
                    // Compact context if approaching token limit
                    if (_compactor.NeedsCompaction(messages))
                    {
                        var compacted = _compactor.CompactIfNeeded(messages);
                        messages = compacted.Messages;
                    }

                    var response = await _modelClient.Complete(messages, ct);
                    step++;
                    Log($"[Step {step}] Model responded ({response.TokensUsed} tokens, {response.ToolCalls.Count} tool calls)");
                    _logger?.Step(step, response.TokensUsed, response.ThinkingTokens,
                        response.ToolCalls.Count, response.ThinkingContent, response.Content);

                    messages.Add(ChatMessage.Assistant(response.Content, response.ToolCalls, response.ThinkingContent));

                    if (response.ToolCalls.Count == 0)
                    {
                        finalOutput = response.Content;
                        state = AgentState.Done;
                    }
                    else
                    {
                        state = AgentState.Observing;
                    }
                    break;

                case AgentState.Observing:
                    Log("[State] Observing — executing tool calls");
                    bool hadError = false;

                    // Get the assistant message with tool calls (guard against null)
                    var lastAssistant = messages.LastOrDefault(m => m.Role == "assistant" && m.ToolCalls?.Count > 0);
                    var toolCalls = lastAssistant?.ToolCalls ?? new List<ToolCall>();

                    foreach (var toolCall in toolCalls)
                    {
                        var detail = FormatToolDetail(toolCall.Name, toolCall.Arguments);
                        Log($"  {toolCall.Name} {detail}");
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var result = await _toolExecutor.Execute(toolCall.Name, toolCall.Arguments);
                        sw.Stop();
                        messages.Add(ChatMessage.FromToolResult(toolCall.Id, result));

                        _logger?.ToolCall(toolCall.Name, detail,
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
                    var lastObservation = FormatLastObservation(messages);
                    if (IsStalled(lastObservation))
                    {
                        Log("[State] Stall detected — stopping");
                        finalOutput = "Stall detected: repeated observations. Stopping.";
                        state = AgentState.Done;
                        break;
                    }

                    if (hadError)
                    {
                        if (errorRecoveryCount < _config.MaxRetries)
                        {
                            errorRecoveryCount++;
                            Log($"[State] Error recovery (attempt {errorRecoveryCount}/{_config.MaxRetries})");
                            state = AgentState.ErrorRecovery;
                        }
                        else
                        {
                            Log($"[State] Max error recovery attempts reached ({errorRecoveryCount}/{_config.MaxRetries}), forcing done");
                            finalOutput = "Max error recovery attempts reached. Stopping.";
                            state = AgentState.Done;
                        }
                    }
                    else
                    {
                        // Successful observation resets the error recovery counter
                        errorRecoveryCount = 0;
                        state = AgentState.Executing;
                    }
                    break;

                case AgentState.ErrorRecovery:
                    // Inject error context as a system message (not a user message)
                    // so the model treats it as instruction rather than conversation
                    var recentErrors = messages
                        .Where(m => m.Role == "tool" && m.ToolResult?.IsError == true)
                        .TakeLast(1)
                        .Select(m => m.Content)
                        .FirstOrDefault() ?? "";
                    var errorHint = string.IsNullOrEmpty(recentErrors)
                        ? "The previous tool call failed. Consider an alternative approach."
                        : $"The previous tool call failed: {recentErrors[..Math.Min(300, recentErrors.Length)]}\nConsider an alternative approach or fix the error before retrying.";
                    messages.Add(ChatMessage.System(errorHint));
                    state = AgentState.Executing;
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