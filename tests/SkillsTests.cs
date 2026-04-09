using LittleHelper;

namespace LittleHelper.Tests;

public class SkillsTests
{
    [Fact]
    public void FormatSkillsBlock_NoSkills_ReturnsEmpty()
    {
        var skills = new SkillDiscovery();
        var result = skills.FormatSkillsBlock();
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FormatSkillsBlock_WithSkills_ReturnsXml()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lh_test_skills_{Guid.NewGuid()}");
        try
        {
            var skillDir = Path.Combine(tempDir, "test-skill");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
                "---\nname: test-skill\ndescription: A test skill\n---\n# Test Skill\n");

            var skills = new SkillDiscovery();
            skills.Discover(null, tempDir);

            var block = skills.FormatSkillsBlock();
            Assert.Contains("<available_skills>", block);
            Assert.Contains("<name>test-skill</name>", block);
            Assert.Contains("<description>A test skill</description>", block);
            Assert.Contains("<location>", block);
            Assert.Contains("Read a skill's file with the read tool", block);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void FormatSkillsBlock_ProjectOverridesBundled()
    {
        // Discover(projectDir, bundledDir) — project overrides bundled by name
        var bundledDir = Path.Combine(Path.GetTempPath(), $"lh_test_bundled_{Guid.NewGuid()}");
        var projectRoot = Path.Combine(Path.GetTempPath(), $"lh_test_project_{Guid.NewGuid()}");
        try
        {
            // Bundled skill (low priority)
            var bundledSkillDir = Path.Combine(bundledDir, "overlap-skill");
            Directory.CreateDirectory(bundledSkillDir);
            File.WriteAllText(Path.Combine(bundledSkillDir, "SKILL.md"),
                "---\nname: overlap-skill\ndescription: Bundled version\n---\n# Bundled\n");

            // Project skill at .little_helper/skills/ inside the project root
            var projSkillDir = Path.Combine(projectRoot, ".little_helper", "skills", "overlap-skill");
            Directory.CreateDirectory(projSkillDir);
            File.WriteAllText(Path.Combine(projSkillDir, "SKILL.md"),
                "---\nname: overlap-skill\ndescription: Project version\n---\n# Project\n");

            var skills = new SkillDiscovery();
            skills.Discover(projectRoot, bundledDir);

            Assert.Single(skills.Skills);
            Assert.Equal("overlap-skill", skills.Skills[0].Name);
            Assert.Equal("Project version", skills.Skills[0].Description);
        }
        finally
        {
            Directory.Delete(bundledDir, true);
            Directory.Delete(projectRoot, true);
        }
    }

    [Fact]
    public void SkillDiscovery_FallbackToDirectoryName_WhenNoFrontmatter()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"lh_test_nofm_{Guid.NewGuid()}");
        try
        {
            var skillDir = Path.Combine(tempDir, "my-cool-skill");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Just a skill without frontmatter\n");

            var skills = new SkillDiscovery();
            skills.Discover(null, tempDir);

            Assert.Single(skills.Skills);
            Assert.Equal("my-cool-skill", skills.Skills[0].Name);
            Assert.Equal("Skill: my-cool-skill", skills.Skills[0].Description);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}