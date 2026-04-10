using System.Text;

namespace LittleHelper;

/// <summary>
/// Builds the system prompt and initial context for agent sessions.
/// Extracted from Agent.cs to keep file sizes under 300 lines (Rule #8).
///
/// Research: system prompt under 1000 tokens, documents-first query-last
/// ordering, operating principles with rationales.
/// </summary>
public class PromptBuilder
{
    private readonly AgentConfig _config;
    private readonly SkillDiscovery _skills;

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
    /// Build minimal system prompt. Research: under 1000 tokens, principles over personas.
    /// </summary>
    private string BuildSystemPrompt()
    {
        var sb = new StringBuilder();

        // Role assignment (one sentence)
        sb.AppendLine("You are a helpful assistant that completes tasks by reading files, running commands, and writing code.");
        sb.AppendLine();

        // Operating principles (numbered, with rationales)
        sb.AppendLine("Operating principles:");
        sb.AppendLine("1. Think before acting. Read existing files before writing code because understanding prevents errors.");
        sb.AppendLine("2. Be concise in output but thorough in reasoning because efficiency matters.");
        sb.AppendLine("3. Prefer editing over rewriting whole files because precision preserves intent.");
        sb.AppendLine("4. Do not re-read files you have already read unless the file may have changed because redundancy wastes steps.");
        sb.AppendLine("5. Test your code before declaring done because verification catches bugs.");
        sb.AppendLine("6. Keep solutions simple and direct because complexity is the enemy of reliability.");
        sb.AppendLine();

        // Tool guidance
        sb.AppendLine("You have access to these tools: read, run, write, search, bash.");
        sb.AppendLine("Use them to complete the task. When done, respond without tool calls.");
        sb.AppendLine();

        // Batch scripting guidance (instead of a separate script tool)
        sb.AppendLine("Efficiency: When you need multiple pieces of information or need to perform");
        sb.AppendLine("several operations, write a short Python or shell script and run it with the");
        sb.AppendLine("run tool. One script call is better than many separate tool calls — it saves");
        sb.AppendLine("round-trips and keeps context clean. Example: instead of 3 separate search");
        sb.AppendLine("calls, write a script that does grep + wc + sort in one shot.");
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
    /// Keeps initial context under ~2000 tokens.
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

            // README.md content if present (helps model understand the project)
            // Truncate at ~6000 chars (~2000 tokens for code-heavy content at chars/3)
            var readmePath = Path.Combine(_config.WorkingDirectory, "README.md");
            if (File.Exists(readmePath))
            {
                var readme = File.ReadAllText(readmePath);
                if (readme.Length > 6000)
                    readme = readme[..6000] + "\n... (truncated)";
                sb.AppendLine("README.md:");
                sb.AppendLine(readme);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(Could not list directory: {ex.Message})");
        }

        return sb.ToString().Trim();
    }
}