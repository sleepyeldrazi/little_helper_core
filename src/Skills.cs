using System.Text;
using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// Skill discovery and formatting. Not execution — skills are prompt injection.
/// Scans SKILL.md files, parses frontmatter, formats the XML block for the system prompt.
///
/// Default skills are seeded (copied) to ~/.little_helper/skills/ on first run.
/// After that, the user owns them — edit, delete, or add new ones freely.
/// </summary>
public class SkillDiscovery
{
    private readonly List<SkillDef> _skills = new();

    /// <summary>Discovered skills, ordered by name.</summary>
    public IReadOnlyList<SkillDef> Skills => _skills;

    /// <summary>User skills directory: ~/.little_helper/skills/</summary>
    public static string UserSkillsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".little_helper", "skills");

    /// <summary>Discover all skills from user-level and project-level directories.</summary>
    public void Discover(string? projectDir = null)
    {
        _skills.Clear();

        // User-level skills: ~/.little_helper/skills/
        ScanDirectory(UserSkillsDir);

        // Project-level skills: .little_helper/skills/ (highest priority, overrides user)
        if (projectDir != null)
        {
            var projDir = Path.Combine(projectDir, ".little_helper", "skills");
            ScanDirectory(projDir);
        }

        // Deduplicate: project overrides user by name
        _skills.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Seed default skills from bundled directory to ~/.little_helper/skills/.
    /// Only copies skills that don't already exist — never overwrites user edits.
    /// </summary>
    public static void SeedDefaults(string bundledDir)
    {
        if (!Directory.Exists(bundledDir))
            return;

        Directory.CreateDirectory(UserSkillsDir);

        foreach (var skillDir in Directory.GetDirectories(bundledDir))
        {
            var skillFile = Path.Combine(skillDir, "SKILL.md");
            if (!File.Exists(skillFile))
                continue;

            var skillName = Path.GetFileName(skillDir);
            var targetDir = Path.Combine(UserSkillsDir, skillName);
            var targetFile = Path.Combine(targetDir, "SKILL.md");

            // Only seed if the skill doesn't already exist
            if (!File.Exists(targetFile))
            {
                Directory.CreateDirectory(targetDir);
                File.Copy(skillFile, targetFile);

                // Copy any supporting files (references, templates, scripts, assets)
                foreach (var file in Directory.GetFiles(skillDir, "*", SearchOption.AllDirectories))
                {
                    if (file == skillFile) continue;
                    var relative = Path.GetRelativePath(skillDir, file);
                    var target = Path.Combine(targetDir, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
            }
        }
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
        catch (Exception)
        {
            // Silently skip malformed skill files
            return null;
        }
    }
}
