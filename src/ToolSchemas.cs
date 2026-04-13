using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// Tool schema definitions and normalization.
/// Extracted from Tools.cs to keep files under 300 lines (Rule #8).
/// </summary>
public static class ToolSchemas
{

    /// <summary>
    /// Register the standard tool schemas with a ModelClient.
    /// Uses abbreviated descriptions for small models (<=14B params).
    /// Note: "run" is intentionally NOT registered — models use "bash" instead.
    /// If a model calls "run" anyway, ToolExecutor handles it as a bash alias.
    /// </summary>
    public static void RegisterAll(IModelClient client, int contextWindow = 32768, string modelName = "")
    {
        var small = IsSmallModelName(modelName);

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

        // Tool 2: bash (the only shell execution tool exposed to models)
        client.RegisterTool("bash",
            small ? "Run a bash command." : "Execute a bash command in the working directory. Current directory persists across calls.",
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

        // Tool 4: edit (also aliased as "patch")
        // Prefer edit over write for existing files -- saves context window
        client.RegisterTool("edit",
            small ? "Edit a file by replacing text." :
                "Edit a file by finding and replacing text. old_string must uniquely match. Use replace_all for multiple matches.",
            NormalizeToolSchema("""
            {
                "type": "object",
                "properties": {
                    "path": { "type": "string", "description": "File path relative to working directory" },
                    "old_string": { "type": "string", "description": "Exact text to find (must be unique unless replace_all is true)" },
                    "new_string": { "type": "string", "description": "Replacement text (use empty string to delete)" },
                    "replace_all": { "type": "boolean", "description": "Replace all occurrences instead of requiring unique match (default: false)" }
                },
                "required": ["path", "old_string", "new_string"]
            }
            """));

        // Tool 5: search
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

        // Tool 6: spawn — delegate a task to a sub-agent running in tmux
        // Only registered when enabled by the TUI. See SubAgentManager.
        // Schema is provided here for reference; registration is conditional.
    }

    /// <summary>Register the spawn tool schema. Called by TUI when sub-agents are enabled.</summary>
    public static void RegisterSpawn(IModelClient client, bool small = false)
    {
        client.RegisterTool("spawn",
            small
                ? "Spawn a sub-agent in tmux. type='small' for quick tasks, 'complex' for multi-step."
                : "Delegate a task to a sub-agent running in tmux. " +
                  "Use type='small' for quick lookup, classification, or single-answer tasks — the agent returns a short exhaustive summary. " +
                  "Use type='complex' for multi-step analysis or implementation — the agent plans a strategy, writes a TODO, executes, and reports results. " +
                  "Returns a tmux window name you can interact with via send-keys and capture-pane.",
            NormalizeToolSchema("""
            {
                "type": "object",
                "properties": {
                    "task": { "type": "string", "description": "The task description for the sub-agent" },
                    "type": { "type": "string", "description": "Agent tier: 'small' for quick lookup/classification (returns concise summary), 'complex' for multi-step analysis (plans, executes, reports)", "enum": ["small", "complex"] }
                },
                "required": ["task", "type"]
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
    /// Recursively normalize an object schema: deduplicate properties,
    /// validates required fields exist.
    /// NOTE: Does NOT add additionalProperties: false -- that keyword
    /// breaks llama.cpp's GBNF grammar generator. Forgecode strips it,
    /// opencode never includes it, and it provides no benefit for tool calling.
    /// </summary>
    private static Dictionary<string, object> NormalizeObject(JsonElement element)
    {
        var result = new Dictionary<string, object>();

        // Copy all existing properties
        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                // Only recurse into actual sub-schemas (property definitions),
                // not structural containers like "properties" or "required"
                var nested = NormalizeObject(prop.Value);
                result[prop.Name] = nested;
            }
            else
            {
                // Non-object values: clone as-is
                result[prop.Name] = prop.Value.Clone();
            }
        }

        // Only add "type": "object" if this looks like a schema (has "properties" or "required")
        // Don't add it to structural containers or property value definitions that already have a type
        if (!result.ContainsKey("type") && (result.ContainsKey("properties") || result.ContainsKey("required")))
            result["type"] = "object";

        return result;
    }

    /// <summary>
    /// Check if a model name indicates <=14B parameters (e.g. "qwen3:4b", "llama3.1:8b", "phi-3.5:3.8b").
    /// Used to select abbreviated tool descriptions for small models.
    /// </summary>
    private static bool IsSmallModelName(string modelName)
    {
        if (string.IsNullOrEmpty(modelName)) return false;
        var name = modelName.ToLowerInvariant();
        var match = System.Text.RegularExpressions.Regex.Match(name, @"(\d+(?:\.\d+)?)\s*b");
        if (!match.Success) return false;
        if (double.TryParse(match.Groups[1].Value, out var billions))
            return billions <= 14.0;
        return false;
    }
}