using System.Text.Json;
using LittleHelper;

namespace LittleHelper.Tests;

public class ToolSchemasTests
{
    private static string ToJson(JsonElement el) =>
        JsonSerializer.Serialize(el);

    [Fact]
    public void NormalizeToolSchema_DoesNotAddAdditionalProperties()
    {
        // NormalizeToolSchema intentionally omits additionalProperties: false
        // because it breaks llama.cpp's GBNF grammar generator
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
        Assert.DoesNotContain("additionalProperties", json);
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
    public void NormalizeToolSchema_NestedObjectNoAdditionalProperties()
    {
        // Nested objects should also NOT get additionalProperties: false
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
        Assert.DoesNotContain("additionalProperties", json);
    }

    [Fact]
    public void NormalizeToolSchema_StringPropertyNotObject_NoExtraProperties()
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
        // Should have exactly 0 additionalProperties (we don't add it at all)
        Assert.DoesNotContain("additionalProperties", json);
    }

    [Fact]
    public void RegisterAll_RegistersFiveTools()
    {
        using var client = new ModelClient("http://localhost:11434/v1", "test");
        ToolSchemas.RegisterAll(client);
        // No exception = pass
    }
}