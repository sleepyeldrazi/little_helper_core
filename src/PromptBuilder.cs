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

    // Context window tiers for prompt compression
    private const int SmallModelThreshold = 16384;  // < 16K: stripped prompt
    private const int TinyModelThreshold = 8192;    // < 8K: minimal prompt

    private bool IsSmallModel => _config.MaxContextTokens < SmallModelThreshold;
    private bool IsTinyModel => _config.MaxContextTokens < TinyModelThreshold || IsModelSizeTiny();

    /// <summary>
    /// Check if the model name contains a parameter count ≤ 8B (e.g. "qwen3:4b", "llama3.1:8b").
    /// Models this small need stripped prompts regardless of context window.
    /// </summary>
    private bool IsModelSizeTiny()
    {
        var name = _config.ModelName.ToLowerInvariant();
        var match = System.Text.RegularExpressions.Regex.Match(name, @"(\d+(?:\.\d+)?)\s*b");
        if (!match.Success) return false;
        if (double.TryParse(match.Groups[1].Value, out var billions))
            return billions <= 8.0;
        return false;
    }

    public PromptBuilder(AgentConfig config, SkillDiscovery skills)
    {
        _config = config;
        _skills = skills;
    }

    /// <summary>
    /// Build the initial context: system prompt, directory listing, and user query.
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
            // Minimal: 3 rules only
            sb.AppendLine("1. Read files before writing code.");
            sb.AppendLine("2. Test your code before declaring done.");
            sb.AppendLine("3. Be concise. Keep solutions simple.");
        }
        else
        {
            sb.AppendLine("1. Think before acting. Read existing files before writing code because understanding prevents errors.");
            sb.AppendLine("2. Be concise in output but thorough in reasoning because efficiency matters.");
            sb.AppendLine("3. Prefer editing over rewriting whole files because precision preserves intent.");
            sb.AppendLine("4. Do not re-read files you have already read unless the file may have changed because redundancy wastes steps.");
            sb.AppendLine("5. Test your code before declaring done because verification catches bugs.");
            sb.AppendLine("6. Keep solutions simple and direct because complexity is the enemy of reliability.");
        }
        sb.AppendLine();

        // Tool guidance
        if (IsTinyModel)
        {
            sb.AppendLine("Tools: read, run, write, search.");
            sb.AppendLine("When done, respond without tool calls.");
        }
        else
        {
            sb.AppendLine("You have access to these tools: read, run, write, search, bash.");
            sb.AppendLine("Use them to complete the task. When done, respond without tool calls.");
        }
        sb.AppendLine();

        // Batch scripting guidance — only for models with enough context to benefit
        if (!IsSmallModel)
        {
            sb.AppendLine("Efficiency: When you need multiple pieces of information or need to perform");
            sb.AppendLine("several operations, write a short Python or shell script and run it with the");
            sb.AppendLine("run tool. One script call is better than many separate tool calls — it saves");
            sb.AppendLine("round-trips and keeps context clean. Example: instead of 3 separate search");
            sb.AppendLine("calls, write a script that does grep + wc + sort in one shot.");
            sb.AppendLine();
        }

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