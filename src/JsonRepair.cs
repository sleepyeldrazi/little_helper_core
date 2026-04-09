using System.Text.Json;

namespace LittleHelper;

/// <summary>
/// JSON repair utilities for handling malformed LLM output.
/// Extracted from ModelClient.cs to keep files under 300 lines (Rule #8).
/// </summary>
static class JsonRepair
{
    /// <summary>
    /// Repair potentially malformed JSON. Strategy:
    /// 1. Try direct parse
    /// 2. Extract from markdown code fences
    /// 3. Find first { or [ and parse from there
    /// 4. Return empty object on total failure
    /// </summary>
    public static JsonElement Repair(string input)
    {
        // Strategy 1: Direct parse
        try
        {
            return JsonDocument.Parse(input).RootElement.Clone();
        }
        catch { }

        // Strategy 2: Extract from markdown code fences
        var fenceMatch = System.Text.RegularExpressions.Regex.Match(
            input, @"```(?:json)?\s*\n?(.*?)\n?\s*```",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (fenceMatch.Success)
        {
            try
            {
                return JsonDocument.Parse(fenceMatch.Groups[1].Value.Trim()).RootElement.Clone();
            }
            catch { }
        }

        // Strategy 3: Find first { or [
        int objStart = input.IndexOf('{');
        int arrStart = input.IndexOf('[');
        int start = -1;

        if (objStart >= 0 && arrStart >= 0)
            start = Math.Min(objStart, arrStart);
        else if (objStart >= 0)
            start = objStart;
        else if (arrStart >= 0)
            start = arrStart;

        if (start >= 0)
        {
            var sub = input.Substring(start);
            try
            {
                return JsonDocument.Parse(sub).RootElement.Clone();
            }
            catch { }
        }

        // All strategies failed — return empty object
        return JsonDocument.Parse("{}").RootElement.Clone();
    }

    /// <summary>Compute Levenshtein distance between two strings.</summary>
    public static int LevenshteinDistance(string a, string b)
    {
        var matrix = new int[a.Length + 1, b.Length + 1];

        for (int i = 0; i <= a.Length; i++) matrix[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) matrix[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        return matrix[a.Length, b.Length];
    }
}