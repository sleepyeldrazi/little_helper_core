using System.Text;
using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// Handles SSE streaming for AnthropicClient. Accumulates content blocks
/// from Anthropic's streaming format (message_start, content_block_start,
/// content_block_delta, content_block_stop, message_delta, message_stop).
/// </summary>
internal static class AnthropicStreaming
{
    public static async Task<JsonElement?> SendStreaming(
        HttpClient http, string endpoint, string requestBody,
        IAgentObserver observer, CancellationToken ct)
    {
        try
        {
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/v1/messages") { Content = content };

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                Console.Error.WriteLine($"Anthropic streaming error ({response.StatusCode}): {errorBody}");
                return null;
            }

            var fullContent = new StringBuilder();
            var fullThinking = new StringBuilder();
            var toolUseBlocks = new Dictionary<string, ToolBlock>(); // id -> accumulating block
            int inputTokens = 0, outputTokens = 0;
            string? currentBlockId = null;

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("event: ")) continue;

                var eventType = line["event: ".Length..].Trim();
                // Next line should be "data: {json}"
                var dataLine = await reader.ReadLineAsync(ct);
                if (dataLine == null || !dataLine.StartsWith("data: ")) continue;
                var data = dataLine["data: ".Length..].Trim();

                JsonDocument doc;
                try { doc = JsonDocument.Parse(data); }
                catch { continue; }

                using (doc)
                {
                    switch (eventType)
                    {
                        case "message_start":
                            if (doc.RootElement.TryGetProperty("message", out var msg))
                            {
                                if (msg.TryGetProperty("usage", out var mu))
                                {
                                    inputTokens = mu.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                                    outputTokens = mu.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                                }
                            }
                            break;

                        case "content_block_start":
                            if (doc.RootElement.TryGetProperty("content_block", out var cbStart))
                            {
                                var blockType = cbStart.TryGetProperty("type", out var bt) ? bt.GetString() : "";
                                if (blockType == "tool_use")
                                {
                                    var id = cbStart.TryGetProperty("id", out var idi) ? idi.GetString() ?? Guid.NewGuid().ToString() : Guid.NewGuid().ToString();
                                    var name = cbStart.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                                    currentBlockId = id;
                                    toolUseBlocks[id] = new ToolBlock(id, name);
                                }
                                else if (blockType == "thinking")
                                {
                                    currentBlockId = "thinking";
                                }
                            }
                            break;

                        case "content_block_delta":
                            if (doc.RootElement.TryGetProperty("delta", out var delta))
                            {
                                var deltaType = delta.TryGetProperty("type", out var dt) ? dt.GetString() : "";
                                switch (deltaType)
                                {
                                    case "text_delta":
                                        if (delta.TryGetProperty("text", out var td) && td.ValueKind != JsonValueKind.Null)
                                        {
                                            var chunk = td.GetString() ?? "";
                                            fullContent.Append(chunk);
                                            observer.OnStreamChunk(chunk, null);
                                        }
                                        break;
                                    case "thinking_delta":
                                        if (delta.TryGetProperty("thinking", out var thd) && thd.ValueKind != JsonValueKind.Null)
                                        {
                                            var chunk = thd.GetString() ?? "";
                                            fullThinking.Append(chunk);
                                            observer.OnStreamChunk("", chunk);
                                        }
                                        break;
                                    case "input_json_delta":
                                        if (delta.TryGetProperty("partial_json", out var pj) && pj.ValueKind != JsonValueKind.Null)
                                        {
                                            var partial = pj.GetString() ?? "";
                                            if (currentBlockId != null && toolUseBlocks.ContainsKey(currentBlockId))
                                                toolUseBlocks[currentBlockId].AppendArgs(partial);
                                        }
                                        break;
                                }
                            }
                            break;

                        case "content_block_stop":
                            currentBlockId = null;
                            break;

                        case "message_delta":
                            if (doc.RootElement.TryGetProperty("usage", out var du))
                            {
                                if (du.TryGetProperty("output_tokens", out var ot2))
                                    outputTokens += ot2.GetInt32();
                            }
                            break;

                        case "message_stop":
                        case "ping":
                            break;
                    }
                }
            }

            return BuildReconstructedResponse(fullContent.ToString(), fullThinking.ToString(),
                toolUseBlocks, inputTokens, outputTokens);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Anthropic streaming failed: {ex.Message}");
            return null;
        }
    }

    private static JsonElement BuildReconstructedResponse(string content, string thinking,
        Dictionary<string, ToolBlock> toolUseBlocks, int inputTokens, int outputTokens)
    {
        // Build an Anthropic-format response that ParseResponse can understand
        var contentBlocks = new List<Dictionary<string, object?>>();

        if (!string.IsNullOrEmpty(thinking))
            contentBlocks.Add(new() { ["type"] = "thinking", ["thinking"] = thinking });
        if (!string.IsNullOrEmpty(content))
            contentBlocks.Add(new() { ["type"] = "text", ["text"] = content });
        foreach (var block in toolUseBlocks.Values)
        {
            contentBlocks.Add(new()
            {
                ["type"] = "tool_use",
                ["id"] = block.Id,
                ["name"] = block.Name,
                ["input"] = JsonSerializer.Deserialize<JsonElement>(block.ArgsBuilder.ToString())
            });
        }

        if (contentBlocks.Count == 0)
            contentBlocks.Add(new() { ["type"] = "text", ["text"] = "" });

        var response = new Dictionary<string, object?>
        {
            ["content"] = contentBlocks,
            ["usage"] = new Dictionary<string, object?>
            {
                ["input_tokens"] = inputTokens,
                ["output_tokens"] = outputTokens
            }
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private class ToolBlock(string id, string name)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public StringBuilder ArgsBuilder { get; } = new();
        public void AppendArgs(string partial) => ArgsBuilder.Append(partial);
    }
}