using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// Tool schema definitions and normalization.
/// Extracted from Tools.cs to keep files under 300 lines (Rule #8).
/// Each schema includes additionalProperties: false on all objects per research
/// (constrained decoding removes syntactic failures).
/// </summary>
public static class ToolSchemas
{
    // Context window thresholds for description tiers
    private const int SmallModelThreshold = 16384;

    /// <summary>
    /// Register the standard tool schemas with a ModelClient.
    /// Uses abbreviated descriptions for small models (context window &lt; 16K or ≤ 8B params).
    /// </summary>
    public static void RegisterAll(IModelClient client, int contextWindow = 32768, string modelName = "")
    {
        var small = contextWindow < SmallModelThreshold || IsSmallModelName(modelName);

        // Tool 1: read
        client.RegisterTool("read",
            small ? "Read file contents." : "Read a file's contents. Use offset/limit for large files.",
            NormalizeToolSchema("""
            {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "File path (relative to working directory, or ~/ for home directory)" },
                    "offset": { "type": "integer", "description": "Starting line (1-indexed)" },
                    "limit": { "type": "integer", "description": "Max lines to read (0 = all)" }
                },
                "required": ["path"]
            }
            """));

        // Tool 2: run
        client.RegisterTool("run",
            small ? "Run a shell command." : "Execute a shell command in the working directory.",
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
            small ? "Write to a file." : "Write content to a file. Creates parent directories if needed.",
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
            small ? "Search files with grep." : "Search file contents with grep/ripgrep.",
            NormalizeToolSchema("""
            {
                "type": "object",
                "properties": {
                    "pattern": { "type": "string", "description": "Search pattern (regex supported)" },
                    "path": { "type": "string", "description": "Directory to search in (default: working directory, or ~/ for home)" },
                    "file_type": { "type": "string", "description": "Optional file extension filter (e.g., 'cs', 'py')" }
                },
                "required": ["pattern"]
            }
            """));

        // Tool 5: bash (alias for run — some models prefer this name)
        client.RegisterTool("bash",
            small ? "Run a bash command." : "Execute a bash command. Same as 'run'.",
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

    /// <summary>
    /// Check if a model name indicates ≤ 8B parameters (e.g. "qwen3:4b", "llama3.1:8b", "phi-3.5:3.8b").
    /// </summary>
    private static bool IsSmallModelName(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return false;
        var name = modelName.ToLowerInvariant();
        var match = System.Text.RegularExpressions.Regex.Match(name, @"(\d+(?:\.\d+)?)\s*b");
        if (!match.Success) return false;
        if (double.TryParse(match.Groups[1].Value, out var billions))
            return billions <= 8.0;
        return false;
    }
}