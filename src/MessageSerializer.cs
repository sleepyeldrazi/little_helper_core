using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// Serializes ChatMessage lists to the OpenAI API JSON format.
/// Extracted from ModelClient.cs to keep files under 300 lines (Rule #8).
/// </summary>
static class MessageSerializer
{
    /// <summary>Convert a list of ChatMessages to the OpenAI messages array format.</summary>
    public static JsonElement ToArray(List<ChatMessage> messages)
    {
        var array = new List<Dictionary<string, object?>>();

        foreach (var msg in messages)
        {
            var entry = new Dictionary<string, object?> { ["role"] = msg.Role };

            if (msg.Content != null)
                entry["content"] = msg.Content;

            if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
            {
                entry["tool_calls"] = msg.ToolCalls.Select(tc => new Dictionary<string, object?>
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = tc.Arguments.GetRawText()
                    }
                }).ToList();
            }

            if (msg.ToolCallId != null)
            {
                entry["tool_call_id"] = msg.ToolCallId;
                if (msg.Content != null)
                    entry["content"] = msg.Content;
            }

            array.Add(entry);
        }

        return JsonSerializer.Deserialize<JsonElement>(
            JsonSerializer.Serialize(array,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }))!;
    }
}