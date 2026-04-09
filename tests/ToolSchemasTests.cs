using System.Text.Json;
using LittleHelper;

namespace LittleHelper.Tests;

public class ToolSchemasTests
{
    private static string ToJson(JsonElement el) =>
        JsonSerializer.Serialize(el);

    [Fact]
    public void NormalizeToolSchema_AddsAdditionalPropertiesFalse_TopLevel()
    {
        var schema = ToolSchemas.NormalizeToolSchema("""
            {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "File path" }
                },
                "required": ["path"]
            }
            """);

        var json = ToJson(schema);
        Assert.Contains("\"additionalProperties\":false", json);
    }

    [Fact]
    public void NormalizeToolSchema_SetsTypeObjectIfMissing()
    {
        var schema = ToolSchemas.NormalizeToolSchema("""
            {
                "properties": {
                    "x": { "type": "integer" }
                }
            }
            """);

        var json = ToJson(schema);
        Assert.Contains("\"type\":\"object\"", json);
    }

    [Fact]
    public void NormalizeToolSchema_DeduplicatesProperties()
    {
        var schema = ToolSchemas.NormalizeToolSchema("""
            {
                "type": "object",
                "properties": {
                    "path": { "type": "string" }
                },
                "required": ["path"]
            }
            """);

        var json = ToJson(schema);
        Assert.Contains("\"path\"", json);
    }

    [Fact]
    public void NormalizeToolSchema_NestedObjectGetsAdditionalPropertiesFalse()
    {
        var schema = ToolSchemas.NormalizeToolSchema("""
            {
                "type": "object",
                "properties": {
                    "filter": {
                        "type": "object",
                        "properties": {
                            "name": { "type": "string" }
                        }
                    }
                }
            }
            """);

        var json = ToJson(schema);
        // Should appear at least twice (top-level + nested object)
        var count = json.Split("\"additionalProperties\":false").Length - 1;
        Assert.True(count >= 2, $"Expected >= 2 additionalProperties:false, got {count}\nJSON: {json}");
    }

    [Fact]
    public void NormalizeToolSchema_StringPropertyNotObject_NoExtraAdditionalProperties()
    {
        var schema = ToolSchemas.NormalizeToolSchema("""
            {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "File path" }
                }
            }
            """);

        var json = ToJson(schema);
        var count = json.Split("\"additionalProperties\":false").Length - 1;
        Assert.Equal(1, count);
    }

    [Fact]
    public void RegisterAll_RegistersFiveTools()
    {
        using var client = new ModelClient("http://localhost:11434/v1", "test");
        ToolSchemas.RegisterAll(client);
        // No exception = pass
    }
}