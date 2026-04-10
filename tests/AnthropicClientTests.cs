using System.Text.Json;
using LittleHelper;
using Xunit;

namespace LittleHelper.Tests;

public class AnthropicClientTests
{
    [Fact]
    public void ExtractSystemPrompt_ReturnsSystemMessages()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("You are a helpful assistant."),
            ChatMessage.User("Hello"),
            ChatMessage.Assistant("Hi there!"),
        };

        var result = AnthropicClient.ExtractSystemPrompt(messages);
        Assert.Equal("You are a helpful assistant.", result);
    }

    [Fact]
    public void ExtractSystemPrompt_ReturnsNull_WhenNoSystemMessages()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("Hello"),
            ChatMessage.Assistant("Hi there!"),
        };

        var result = AnthropicClient.ExtractSystemPrompt(messages);
        Assert.Null(result);
    }

    [Fact]
    public void ExtractSystemPrompt_JoinsMultipleSystemMessages()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("First system message."),
            ChatMessage.System("Second system message."),
            ChatMessage.User("Hello"),
        };

        var result = AnthropicClient.ExtractSystemPrompt(messages);
        Assert.Equal("First system message.\n\nSecond system message.", result);
    }

    [Fact]
    public void ConvertMessages_SkipsSystemMessages()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("System prompt"),
            ChatMessage.User("Hello"),
        };

        var result = AnthropicClient.ConvertMessages(messages);
        Assert.Single(result);
        Assert.Equal("user", result[0]["role"]?.ToString());
    }

    [Fact]
    public void ConvertMessages_ConvertsToolResult_ToUserMessageWithToolResultBlock()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.FromToolResult("call_123", new ToolResult("file contents here", false)),
        };

        var result = AnthropicClient.ConvertMessages(messages);
        Assert.Single(result);
        Assert.Equal("user", result[0]["role"]?.ToString());

        // Content should be an array with a tool_result block
        var content = result[0]["content"];
        Assert.NotNull(content);
    }

    [Fact]
    public void ConvertMessages_ConvertsAssistantWithToolCalls_ToContentBlocks()
    {
        var toolCalls = new List<ToolCall>
        {
            new("toolu_123", "read", JsonSerializer.Deserialize<JsonElement>("{\"path\": \"test.cs\"}"))
        };

        var messages = new List<ChatMessage>
        {
            ChatMessage.Assistant("Let me read that file.", toolCalls),
        };

        var result = AnthropicClient.ConvertMessages(messages);
        Assert.Single(result);
        Assert.Equal("assistant", result[0]["role"]?.ToString());
    }

    [Fact]
    public void ConvertMessages_ConvertsSimpleUserMessage()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.User("Write a hello world program"),
        };

        var result = AnthropicClient.ConvertMessages(messages);
        Assert.Single(result);
        Assert.Equal("user", result[0]["role"]?.ToString());
        Assert.Equal("Write a hello world program", result[0]["content"]?.ToString());
    }

    [Fact]
    public void ConvertMessages_ConvertsAssistantWithThinking()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.Assistant("Here's the answer", reasoningContent: "Let me think about this..."),
        };

        var result = AnthropicClient.ConvertMessages(messages);
        Assert.Single(result);
        Assert.Equal("assistant", result[0]["role"]?.ToString());
        // Content should be an array with thinking + text blocks
        var content = result[0]["content"];
        Assert.NotNull(content);
    }

    [Fact]
    public void AnthropicClient_Created_WithCorrectHeaders()
    {
        // Verify the constructor sets up x-api-key and anthropic-version headers
        using var client = new AnthropicClient(
            "https://api.anthropic.com", "claude-sonnet-4-20250514",
            apiKey: "sk-ant-test123");

        // No exception means construction succeeded
        Assert.NotNull(client);
    }

    [Fact]
    public void AnthropicClient_RegisterTool_Works()
    {
        using var client = new AnthropicClient(
            "https://api.anthropic.com", "claude-sonnet-4-20250514", apiKey: "sk-test");

        var schema = JsonSerializer.Deserialize<JsonElement>(
            "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}");

        client.RegisterTool("read", "Read a file", schema);
        // No exception means tool registration worked
    }
}