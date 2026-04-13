using System.Text;

namespace LittleHelper;

/// <summary>
/// Builds the system prompt and initial context for agent sessions.
/// Extracted from Agent.cs to keep file sizes under 300 lines (Rule #8).
///
/// Research: system prompt under 1000 tokens, documents-first query-last
/// ordering, operating principles with rationales.
///
/// Dynamic compression: adjusts prompt verbosity based on context window size.
/// Small context (&lt;16K) = small model = stripped prompt.
/// Large context (&gt;=32K) = full prompt with README and batch scripting hint.
/// </summary>
public class PromptBuilder
{
    private readonly AgentConfig _config;
    private readonly SkillDiscovery _skills;

    // Model size thresholds for prompt tiering (parameter count in billions)
    // Research-backed: tiny models need stripped prompts, large models can use full context.
    // Bench data: Tiny best for <=14B, Small best for 14-35B, Full for >35B.
    private const double TinyModelMaxB = 14.0;    // <= 14B: minimal prompt, no rationales
    private const double SmallModelMaxB = 35.0;   // <= 35B: principles with rationales, no batch hint

    // Resolved tier from config override ("tiny"/"small"/"full") or null for auto
    private readonly string? _forcedTier;

    /// <summary>Effective tier: override from config if set, otherwise auto-detect from model name.</summary>
    private string EffectiveTier
    {
        get
        {
            if (_forcedTier != null) return _forcedTier;
            if (IsModelSizeAtMost(TinyModelMaxB)) return "tiny";
            if (IsModelSizeAtMost(SmallModelMaxB)) return "small";
            return "full";
        }
    }

    private bool IsTinyModel => EffectiveTier == "tiny";
    private bool IsSmallModel => EffectiveTier == "tiny" || EffectiveTier == "small";

    /// <summary>
    /// Check if the model name indicates a parameter count at or below the given threshold.
    /// Matches patterns like "qwen3:14b", "llama3.1:8b", "qwen3.5:35b", "gemma3:4b".
    /// Models without a B suffix (frontier/proprietary) default to Full tier.
    /// </summary>
    private bool IsModelSizeAtMost(double maxBillions)
    {
        var name = _config.ModelName.ToLowerInvariant();
        var match = System.Text.RegularExpressions.Regex.Match(name, @"(\d+(?:\.\d+)?)\s*b");
        if (!match.Success) return false;
        if (double.TryParse(match.Groups[1].Value, out var billions))
            return billions <= maxBillions;
        return false;
    }

    public PromptBuilder(AgentConfig config, SkillDiscovery skills)
    {
        _config = config;
        _skills = skills;

        // Resolve prompt tier override: normalize "auto"/null to null (auto-detect), anything else is forced
        var tier = config.PromptTier?.Trim().ToLowerInvariant();
        _forcedTier = (tier is null or "auto" or "") ? null : tier;
    }

    /// <summary>
    /// Build the initial context: system prompt, working directory context (full models only), and user query.
    /// Research: Lost in the Middle — query at end yields up to 30% improvement.
    /// </summary>
    public List<ChatMessage> BuildInitialContext(string userPrompt)
    {
        var messages = new List<ChatMessage>();

        // System prompt: minimal, constraint-based, no examples
        var systemPrompt = BuildSystemPrompt();
        messages.Add(ChatMessage.System(systemPrompt));

        // Initial context: directory structure + README if present
        var context = BuildWorkingDirectoryContext();
        if (!string.IsNullOrEmpty(context))
        {
            messages.Add(ChatMessage.User(context));
        }

        // User query last (Lost in the Middle: query at end = up to 30% improvement)
        messages.Add(ChatMessage.User(userPrompt));

        return messages;
    }

    /// <summary>
    /// Build system prompt. Adjusts verbosity based on context window size.
    /// Research: under 1000 tokens, principles over personas.
    /// </summary>
    private string BuildSystemPrompt()
    {
        var sb = new StringBuilder();

        // Role assignment (one sentence)
        sb.AppendLine("You are a helpful assistant that completes tasks by reading files, running commands, and writing code.");
        sb.AppendLine();

        // Operating principles — abbreviated for small models
        sb.AppendLine("Operating principles:");
        if (IsTinyModel)
        {
            // Minimal: 3 rules + planning hint
            sb.AppendLine("1. Read files before writing code.");
            sb.AppendLine("2. Plan before acting on multi-step tasks.");
            sb.AppendLine("3. Test your code before declaring done.");
        }
        else
        {
            sb.AppendLine("1. Think before acting. Read existing files before writing code because understanding prevents errors.");
            sb.AppendLine("2. When given a multi-step or complex task, first identify each goal, then create a step-by-step plan that chronologically addresses each task before beginning work.");
            sb.AppendLine("3. Prefer the edit tool over rewriting whole files with write because it saves tokens and preserves unchanged code.");
            sb.AppendLine("4. Do not re-read files you have already read unless the file may have changed because redundancy wastes steps.");
            sb.AppendLine("5. Test your code before declaring done because verification catches bugs.");
            sb.AppendLine("6. Keep solutions simple and direct because complexity is the enemy of reliability.");
        }
        sb.AppendLine();

        // Tool guidance
        if (IsTinyModel)
        {
            sb.AppendLine("Tools: read, bash, write, edit, search.");
            sb.AppendLine("When done, respond without tool calls.");
        }
        else
        {
            sb.AppendLine("You have access to these tools: read, bash, write, edit, search.");
            sb.AppendLine("Use them to complete the task. When done, respond without tool calls.");
        }
        sb.AppendLine();

        // Working directory context
        sb.AppendLine($"Working directory: {_config.WorkingDirectory}");

        // Skills block (progressive disclosure - model reads with `read` tool when needed)
        var skillsBlock = _skills.FormatSkillsBlock();
        if (!string.IsNullOrEmpty(skillsBlock))
        {
            sb.AppendLine();
            sb.AppendLine(skillsBlock);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build context about the working directory: file listing and README contents.
    /// Skips README for small models, truncates aggressively for tiny models.
    /// </summary>
    private string BuildWorkingDirectoryContext()
    {
        var sb = new StringBuilder();

        try
        {
            // Directory structure (names only, not contents)
            // Only include for full-context models — tiny/small models get stripped context
            if (!IsSmallModel)
            {
                var files = Directory.GetFiles(_config.WorkingDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Select(f => Path.GetFileName(f))
                    .OrderBy(f => f)
                    .ToList();

                var dirs = Directory.GetDirectories(_config.WorkingDirectory, "*", SearchOption.TopDirectoryOnly)
                    .Select(d => Path.GetFileName(d))
                    .OrderBy(d => d)
                    .ToList();

                if (files.Count > 0 || dirs.Count > 0)
                {
                    sb.AppendLine("Files in working directory:");
                    foreach (var dir in dirs)
                        sb.AppendLine($"  {dir}/");
                    foreach (var file in files)
                        sb.AppendLine($"  {file}");
                    sb.AppendLine();
                }
            }

            // README: skip for small models, truncate for tiny models
            if (!IsSmallModel)
            {
                var readmePath = Path.Combine(_config.WorkingDirectory, "README.md");
                if (File.Exists(readmePath))
                {
                    var readme = File.ReadAllText(readmePath);
                    var maxChars = IsTinyModel ? 1500 : 6000;
                    if (readme.Length > maxChars)
                        readme = readme[..maxChars] + "\n... (truncated)";
                    sb.AppendLine("README.md:");
                    sb.AppendLine(readme);
                }
            }

            // Project instructions: AGENTS.md, CLAUDE.md, .cursorrules
            // These provide project-specific rules that override general principles.
            if (!IsTinyModel)
            {
                var instructionFiles = new[]
                {
                    ("AGENTS.md", "Project instructions (AGENTS.md)"),
                    ("CLAUDE.md", "Project instructions (CLAUDE.md)"),
                    (".cursorrules", "Project instructions (.cursorrules)")
                };

                foreach (var (fileName, label) in instructionFiles)
                {
                    var filePath = Path.Combine(_config.WorkingDirectory, fileName);
                    if (File.Exists(filePath))
                    {
                        var content = File.ReadAllText(filePath);
                        var maxChars = IsSmallModel ? 1500 : 4000;
                        if (content.Length > maxChars)
                            content = content[..maxChars] + "\n... (truncated)";
                        sb.AppendLine();
                        sb.AppendLine($"{label}:");
                        sb.AppendLine(content);
                        break; // Only inject the first one found
                    }
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(Could not list directory: {ex.Message})");
        }

        return sb.ToString().Trim();
    }
}