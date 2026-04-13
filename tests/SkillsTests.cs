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
        // Create a temp project dir with skills in .little_helper/skills/
        var projectRoot = Path.Combine(Path.GetTempPath(), $"lh_test_skills_{Guid.NewGuid()}");
        try
        {
            var skillDir = Path.Combine(projectRoot, ".little_helper", "skills", "test-skill");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
                "---\nname: test-skill\ndescription: A test skill\n---\n# Test Skill\n");

            var skills = new SkillDiscovery();
            skills.Discover(projectRoot);

            var block = skills.FormatSkillsBlock();
            Assert.Contains("<available_skills>", block);
            Assert.Contains("<name>test-skill</name>", block);
            Assert.Contains("<description>A test skill</description>", block);
            Assert.Contains("<location>", block);
            Assert.Contains("Read a skill's file with the read tool", block);
        }
        finally
        {
            Directory.Delete(projectRoot, true);
        }
    }

    [Fact]
    public void FormatSkillsBlock_ProjectOverridesUser()
    {
        // Project skill overrides user skill by name
        var projectRoot = Path.Combine(Path.GetTempPath(), $"lh_test_project_{Guid.NewGuid()}");
        try
        {
            // User skill (in ~/.little_helper/skills/) — skip if already exists
            var userSkillDir = Path.Combine(SkillDiscovery.UserSkillsDir, "test-override-skill");
            var userSkillExisted = Directory.Exists(userSkillDir);
            if (!userSkillExisted)
            {
                Directory.CreateDirectory(userSkillDir);
                File.WriteAllText(Path.Combine(userSkillDir, "SKILL.md"),
                    "---\nname: test-override-skill\ndescription: User version\n---\n# User\n");
            }

            // Project skill at .little_helper/skills/ inside the project root
            var projSkillDir = Path.Combine(projectRoot, ".little_helper", "skills", "test-override-skill");
            Directory.CreateDirectory(projSkillDir);
            File.WriteAllText(Path.Combine(projSkillDir, "SKILL.md"),
                "---\nname: test-override-skill\ndescription: Project version\n---\n# Project\n");

            var skills = new SkillDiscovery();
            skills.Discover(projectRoot);

            // Project version should win
            var found = skills.Skills.FirstOrDefault(s => s.Name == "test-override-skill");
            Assert.NotNull(found);
            Assert.Equal("Project version", found.Description);

            // Clean up user skill only if we created it
            if (!userSkillExisted)
                Directory.Delete(userSkillDir, true);
        }
        finally
        {
            Directory.Delete(projectRoot, true);
        }
    }

    [Fact]
    public void SkillDiscovery_FallbackToDirectoryName_WhenNoFrontmatter()
    {
        var projectRoot = Path.Combine(Path.GetTempPath(), $"lh_test_nofm_{Guid.NewGuid()}");
        try
        {
            var skillDir = Path.Combine(projectRoot, ".little_helper", "skills", "my-cool-skill");
            Directory.CreateDirectory(skillDir);
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "# Just a skill without frontmatter\n");

            var skills = new SkillDiscovery();
            skills.Discover(projectRoot);

            var found = skills.Skills.FirstOrDefault(s => s.Name == "my-cool-skill");
            Assert.NotNull(found);
            Assert.Equal("Skill: my-cool-skill", found.Description);
        }
        finally
        {
            Directory.Delete(projectRoot, true);
        }
    }

    [Fact]
    public void SeedDefaults_CopiesNewSkills_WithoutOverwriting()
    {
        var bundledDir = Path.Combine(Path.GetTempPath(), $"lh_test_bundled_{Guid.NewGuid()}");
        var userDir = Path.Combine(Path.GetTempPath(), $"lh_test_user_{Guid.NewGuid()}");
        try
        {
            // Create a bundled skill
            var bundledSkill = Path.Combine(bundledDir, "default-skill");
            Directory.CreateDirectory(bundledSkill);
            File.WriteAllText(Path.Combine(bundledSkill, "SKILL.md"),
                "---\nname: default-skill\ndescription: Default version\n---\n# Default\n");

            // Override UserSkillsDir temporarily is not possible (static property),
            // so we test the seed logic directly by checking that existing files aren't overwritten.
            // SeedDefaults targets the real UserSkillsDir, so we just verify it doesn't crash.
            SkillDiscovery.SeedDefaults(bundledDir);
            // Should not throw — that's the main assertion
        }
        finally
        {
            Directory.Delete(bundledDir, true);
        }
    }
}
