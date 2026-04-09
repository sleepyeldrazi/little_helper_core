using System.Text.Json;
using LittleHelper;

namespace LittleHelper.Tests;

public class TypesTests
{
    [Fact]
    public void AgentConfig_Default_HasExpectedValues()
    {
        var config = AgentConfig.Default;
        Assert.Equal("http://localhost:11434/v1", config.ModelEndpoint);
        Assert.Equal("qwen3:14b", config.ModelName);
        Assert.Equal(32768, config.MaxContextTokens);
        Assert.Equal(30, config.MaxSteps);
        Assert.Equal(2, config.MaxRetries);
        Assert.Equal(5, config.StallThreshold);
        Assert.Equal(0.3, config.Temperature);
    }

    [Fact]
    public void ChatMessage_FactoryMethods_CreateCorrectRoles()
    {
        var sys = ChatMessage.System("You are helpful");
        Assert.Equal("system", sys.Role);
        Assert.Equal("You are helpful", sys.Content);

        var user = ChatMessage.User("Hello");
        Assert.Equal("user", user.Role);
        Assert.Equal("Hello", user.Content);

        var assistant = ChatMessage.Assistant("Hi there", null);
        Assert.Equal("assistant", assistant.Role);
        Assert.Equal("Hi there", assistant.Content);

        var toolResult = ChatMessage.FromToolResult("call_1", new ToolResult("output", false));
        Assert.Equal("tool", toolResult.Role);
        Assert.Equal("call_1", toolResult.ToolCallId);
    }

    [Fact]
    public void ToolCall_And_ToolResult_Records_HaveValueEquality()
    {
        var tc1 = new ToolCall("id1", "read", JsonDocument.Parse("{\"path\":\"a.cs\"}").RootElement);
        var tc2 = new ToolCall("id1", "read", JsonDocument.Parse("{\"path\":\"a.cs\"}").RootElement);
        // JsonElement doesn't do value equality, but Id and Name should match
        Assert.Equal(tc1.Id, tc2.Id);
        Assert.Equal(tc1.Name, tc2.Name);

        var tr1 = new ToolResult("output", false, "/tmp/a.cs");
        var tr2 = tr1 with { IsError = true };
        Assert.True(tr1.IsError != tr2.IsError);
        Assert.Equal(tr1.Output, tr2.Output);
    }

    [Fact]
    public void AgentResult_Records_Work()
    {
        var result = new AgentResult(true, "Done", new List<string> { "a.cs", "b.cs" });
        Assert.True(result.Success);
        Assert.Equal("Done", result.Output);
        Assert.Equal(2, result.FilesChanged.Count);
        Assert.Empty(result.ThinkingLog);
        Assert.Equal(0, result.TotalThinkingTokens);
    }

    [Fact]
    public void AgentResult_WithThinking_TracksCorrectly()
    {
        var thinking = new List<string> { "Hmm, let me think...", "OK so the answer is..." };
        var result = new AgentResult(true, "Done", new List<string>(),
            ThinkingLog: thinking, TotalThinkingTokens: 500);
        Assert.Equal(2, result.ThinkingLog.Count);
        Assert.Equal(500, result.TotalThinkingTokens);
    }

    [Fact]
    public void ModelResponse_DefaultThinkingIsNull()
    {
        var response = new ModelResponse("hello", new List<ToolCall>(), 100);
        Assert.Null(response.ThinkingContent);
        Assert.Equal(0, response.ThinkingTokens);
    }

    [Fact]
    public void ModelResponse_WithThinking()
    {
        var response = new ModelResponse("hello", new List<ToolCall>(), 100,
            ThinkingContent: "I need to think about this...", ThinkingTokens: 50);
        Assert.Equal("I need to think about this...", response.ThinkingContent);
        Assert.Equal(50, response.ThinkingTokens);
    }

    [Fact]
    public void CompactionResult_Records_Work()
    {
        var messages = new List<ChatMessage> { ChatMessage.System("hello") };
        var result = new CompactionResult(messages, 100);
        Assert.Single(result.Messages);
        Assert.Equal(100, result.TokensSaved);
    }

    [Fact]
    public void SkillDef_Records_Work()
    {
        var skill = new SkillDef("verify", "Run build commands", "/path/verify/SKILL.md");
        Assert.Equal("verify", skill.Name);
        Assert.Equal("Run build commands", skill.Description);
    }
}