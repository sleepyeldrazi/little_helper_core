using System.Text;
using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// Handles SSE streaming for ModelClient. Accumulates chunks into a complete response
/// while forwarding content/thinking deltas to an observer.
/// Extracted from ModelClient to keep each file under 300 lines (Rule #8).
/// </summary>
internal static class ModelStreaming
{
    /// <summary>
    /// Send a streaming SSE request. Reads the response chunk by chunk, accumulates
    /// content/thinking/tool_calls, forwards deltas to the observer, and returns
    /// a reconstructed non-streaming JsonElement for ParseResponse.
    /// </summary>
    public static async Task<JsonElement?> SendStreaming(
        HttpClient http, string endpoint, string requestBody,
        IAgentObserver observer, CancellationToken ct)
    {
        try
        {
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint}/chat/completions") { Content = content };

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                observer.OnError($"Model API error ({response.StatusCode}): {errorBody}");
                return null;
            }

            // Check Content-Type: if the API doesn't support streaming, it returns
            // application/json instead of text/event-stream. Fall back gracefully.
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("application/json") && !contentType.Contains("event-stream"))
            {
                // Non-SSE response — the API ignored stream:true or doesn't support it.
                // Parse it as a regular response directly.
                var json = await response.Content.ReadAsStringAsync(ct);
                try
                {
                    return JsonDocument.Parse(json).RootElement;
                }
                catch (Exception ex)
                {
                    observer.OnError($"Failed to parse non-streaming response: {ex.Message}");
                    return null;
                }
            }

            // Accumulators for the full response
            var fullContent = new StringBuilder();
            var fullThinking = new StringBuilder();
            var toolCallArgs = new Dictionary<int, StringBuilder>();
            var toolCallNames = new Dictionary<int, string>();
            var toolCallIds = new Dictionary<int, string>();
            int totalTokens = 0;

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: ")) continue;
                var data = line["data: ".Length..].Trim();
                if (data == "[DONE]") break;

                JsonDocument doc;
                try { doc = JsonDocument.Parse(data); }
                catch { continue; }

                using (doc)
                {
                    var root = doc.RootElement;

                    if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                    {
                        var delta = choices[0].GetProperty("delta");

                        // Content delta
                        if (delta.TryGetProperty("content", out var c) && c.ValueKind != JsonValueKind.Null)
                        {
                            var chunk = c.GetString() ?? "";
                            fullContent.Append(chunk);
                            observer.OnStreamChunk(chunk, null);
                        }

                        // Thinking/reasoning delta (Kimi K2.5 / DeepSeek)
                        if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind != JsonValueKind.Null)
                        {
                            var chunk = rc.GetString() ?? "";
                            fullThinking.Append(chunk);
                            observer.OnStreamChunk("", chunk);
                        }
                        // Ollama thinking delta
                        else if (delta.TryGetProperty("thinking", out var tk) && tk.ValueKind != JsonValueKind.Null)
                        {
                            var chunk = tk.GetString() ?? "";
                            fullThinking.Append(chunk);
                            observer.OnStreamChunk("", chunk);
                        }

                        // Tool call deltas (streaming)
                        if (delta.TryGetProperty("tool_calls", out var tcDelta) && tcDelta.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tc in tcDelta.EnumerateArray())
                            {
                                var idx = tc.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : 0;
                                if (!toolCallArgs.ContainsKey(idx))
                                {
                                    toolCallArgs[idx] = new StringBuilder();
                                    toolCallNames[idx] = "";
                                    toolCallIds[idx] = "";
                                }
                                if (tc.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null)
                                    toolCallIds[idx] = id.GetString() ?? "";
                                if (tc.TryGetProperty("function", out var func))
                                {
                                    if (func.TryGetProperty("name", out var name) && name.ValueKind != JsonValueKind.Null)
                                        toolCallNames[idx] = name.GetString() ?? "";
                                    if (func.TryGetProperty("arguments", out var args) && args.ValueKind != JsonValueKind.Null)
                                        toolCallArgs[idx].Append(args.GetString() ?? "");
                                }
                            }
                        }
                    }

                    // Usage from streaming (some providers send it in the last chunk)
                    if (root.TryGetProperty("usage", out var usage))
                    {
                        if (usage.TryGetProperty("total_tokens", out var total))
                            totalTokens = total.GetInt32();
                    }
                }
            }

            return BuildReconstructedResponse(fullContent.ToString(), fullThinking.ToString(),
                toolCallIds, toolCallNames, toolCallArgs, totalTokens);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            observer.OnError($"Streaming request failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Build a synthetic non-streaming response from accumulated stream data.</summary>
    internal static JsonElement BuildReconstructedResponse(string content, string thinking,
        Dictionary<int, string> toolCallIds, Dictionary<int, string> toolCallNames,
        Dictionary<int, StringBuilder> toolCallArgs, int totalTokens)
    {
        var toolCallsList = new List<Dictionary<string, object>>();
        foreach (var idx in toolCallArgs.Keys.OrderBy(k => k))
        {
            var func = new Dictionary<string, object>
            {
                ["name"] = toolCallNames.GetValueOrDefault(idx, ""),
                ["arguments"] = toolCallArgs[idx].ToString()
            };
            var tc = new Dictionary<string, object>
            {
                ["id"] = toolCallIds.GetValueOrDefault(idx, Guid.NewGuid().ToString()),
                ["type"] = "function",
                ["function"] = func
            };
            toolCallsList.Add(tc);
        }

        var message = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = content
        };
        if (!string.IsNullOrEmpty(thinking))
            message["reasoning_content"] = thinking;
        if (toolCallsList.Count > 0)
            message["tool_calls"] = toolCallsList;

        var response = new Dictionary<string, object?>
        {
            ["choices"] = new[]
            {
                new Dictionary<string, object?>
                {
                    ["message"] = message,
                    ["finish_reason"] = "stop",
                    ["index"] = 0
                }
            },
            ["usage"] = new Dictionary<string, object?>
            {
                ["total_tokens"] = totalTokens
            }
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });
        return JsonDocument.Parse(json).RootElement.Clone();
    }
}