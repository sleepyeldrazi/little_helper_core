using System.Text;
using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// Skill discovery and formatting. Not execution — skills are prompt injection.
/// Scans SKILL.md files, parses frontmatter, formats the XML block for the system prompt.
/// </summary>
public class SkillDiscovery
{
    private readonly List<SkillDef> _skills = new();

    /// <summary>Discovered skills, ordered by name.</summary>
    public IReadOnlyList<SkillDef> Skills => _skills;

    /// <summary>Discover all skills from bundled, user-level and project-level directories.</summary>
    public void Discover(string? projectDir = null, string? bundledDir = null)
    {
        _skills.Clear();

        // Bundled skills (lowest priority)
        if (bundledDir != null && Directory.Exists(bundledDir))
        {
            ScanDirectory(bundledDir);
        }

        // User-level skills: ~/.little_helper/skills/
        var userDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".little_helper", "skills");
        ScanDirectory(userDir);

        // Project-level skills: .little_helper/skills/ (highest priority)
        if (projectDir != null)
        {
            var projDir = Path.Combine(projectDir, ".little_helper", "skills");
            ScanDirectory(projDir);
        }

        // Deduplicate: later directories override earlier by name
        _skills.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Format the available_skills XML block for the system prompt.
/// Model reads skill files with the read tool when it needs them.
/// </summary>
    public string FormatSkillsBlock()
    {
        if (_skills.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("<available_skills>");
        foreach (var skill in _skills)
        {
            sb.AppendLine("  <skill>");
            sb.AppendLine($"    <name>{skill.Name}</name>");
            sb.AppendLine($"    <description>{skill.Description}</description>");
            sb.AppendLine($"    <location>{skill.FilePath}</location>");
            sb.AppendLine("  </skill>");
        }
        sb.AppendLine("</available_skills>");
        sb.AppendLine("Read a skill's file with the read tool when you want to use it.");
        return sb.ToString();
    }

    /// <summary>Scan a directory for SKILL.md files.</summary>
    private void ScanDirectory(string dir)
    {
        if (!Directory.Exists(dir))
            return;

        foreach (var skillDir in Directory.GetDirectories(dir))
        {
            var skillFile = Path.Combine(skillDir, "SKILL.md");
            if (File.Exists(skillFile))
            {
                var skill = ParseSkillFile(skillFile);
                if (skill != null)
                {
                    // Remove existing skill with same name (project overrides user)
                    var existing = _skills.FindIndex(s => s.Name == skill.Name);
                    if (existing >= 0)
                        _skills[existing] = skill;
                    else
                        _skills.Add(skill);
                }
            }
        }
    }

    /// <summary>
    /// Parse a SKILL.md file. Extracts name and description from YAML frontmatter.
    /// Frontmatter format:
    /// ---
    /// name: skill-name
    /// description: Short description
    /// ---
    /// </summary>
    private static SkillDef? ParseSkillFile(string filePath)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var name = "";
            var description = "";

            // Extract YAML frontmatter
            if (content.StartsWith("---"))
            {
                var end = content.IndexOf("---", 3, StringComparison.Ordinal);
                if (end > 0)
                {
                    var frontmatter = content.Substring(3, end - 3).Trim();

                    foreach (var line in frontmatter.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                            name = trimmed.Substring(5).Trim();
                        else if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                            description = trimmed.Substring(12).Trim();
                    }
                }
            }

            // Fallback: use directory name if frontmatter missing
            if (string.IsNullOrWhiteSpace(name))
                name = new DirectoryInfo(Path.GetDirectoryName(filePath)!).Name;

            if (string.IsNullOrWhiteSpace(description))
                description = $"Skill: {name}";

            return new SkillDef(name, description, filePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error parsing skill file {filePath}: {ex.Message}");
            return null;
        }
    }
}
