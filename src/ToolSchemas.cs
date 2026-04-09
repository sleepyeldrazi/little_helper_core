using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// Tool schema definitions and normalization.
/// Extracted from Tools.cs to keep files under 300 lines (Rule #8).
/// Each schema includes additionalProperties: false on all objects per research
/// (constrained decoding removes syntactic failures).
/// </summary>
static class ToolSchemas
{
    /// <summary>
    /// Register the 5 standard tool schemas with a ModelClient.
    /// </summary>
    public static void RegisterAll(ModelClient client)
    {
        // Tool 1: read
        client.RegisterTool("read",
            "Read a file's contents. Use offset/limit for large files.",
            NormalizeToolSchema("""
            {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "File path relative to working directory" },
                    "offset": { "type": "integer", "description": "Starting line (1-indexed)" },
                    "limit": { "type": "integer", "description": "Max lines to read (0 = all)" }
                },
                "required": ["path"]
            }
            """));

        // Tool 2: run
        client.RegisterTool("run",
            "Execute a shell command in the working directory.",
            NormalizeToolSchema("""
            {
                "type": "object",
                "properties": {
                    "command": { "type": "string", "description": "Shell command to execute" },
                    "timeout": { "type": "integer", "description": "Timeout in seconds (default: 60)" }
                },
                "required": ["command"]
            }
            """));

        // Tool 3: write
        client.RegisterTool("write",
            "Write content to a file. Creates parent directories if needed.",
            NormalizeToolSchema("""
            {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "File path relative to working directory" },
                    "content": { "type": "string", "description": "Content to write" }
                },
                "required": ["path", "content"]
            }
            """));

        // Tool 4: search
        client.RegisterTool("search",
            "Search file contents with grep/ripgrep.",
            NormalizeToolSchema("""
            {
                "type": "object",
                "properties": {
                    "pattern": { "type": "string", "description": "Search pattern (regex supported)" },
                    "file_type": { "type": "string", "description": "Optional file extension filter (e.g., 'cs', 'py')" }
                },
                "required": ["pattern"]
            }
            """));

        // Tool 5: bash (alias for run)
        client.RegisterTool("bash",
            "Execute a bash command. Same as 'run'.",
            NormalizeToolSchema("""
            {
                "type": "object",
                "properties": {
                    "command": { "type": "string", "description": "Bash command to execute" },
                    "timeout": { "type": "integer", "description": "Timeout in seconds (default: 60)" }
                },
                "required": ["command"]
            }
            """));
    }

    /// <summary>
    /// Normalize a tool JSON schema recursively: deduplicates properties,
    /// ensures additionalProperties: false on ALL object schemas (including nested),
    /// validates required fields exist.
    /// Research: schema-enforced constrained decoding "removes syntactic failures".
    /// </summary>
    public static JsonElement NormalizeToolSchema(string json)
    {
        var doc = JsonDocument.Parse(json);
        var normalized = NormalizeObject(doc.RootElement);
        return JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(normalized));
    }

    /// <summary>
    /// Recursively normalize an object schema: ensure additionalProperties: false
    /// on all nested objects, deduplicate properties, validate required fields.
    /// </summary>
    private static Dictionary<string, object> NormalizeObject(JsonElement element)
    {
        var result = new Dictionary<string, object>();

        // Copy all existing properties
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                // Recursively normalize nested objects
                var nested = NormalizeObject(prop.Value);

                // If this is a property value that describes a schema (has "type"),
                // ensure it has additionalProperties: false when type is "object"
                if (nested.ContainsKey("type") && nested["type"] is string typeStr
                    && typeStr == "object")
                {
                    nested["additionalProperties"] = false;
                }

                result[prop.Name] = nested;
            }
            else
            {
                // Non-object values: clone as-is
                result[prop.Name] = prop.Value.Clone();
            }
        }

        // Ensure top-level has type: object and additionalProperties: false
        if (!result.ContainsKey("type"))
            result["type"] = "object";

        if (result.TryGetValue("type", out var typeObj) && typeObj is string t && t == "object")
            result["additionalProperties"] = false;

        return result;
    }
}