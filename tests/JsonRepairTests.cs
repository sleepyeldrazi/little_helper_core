using System.Text.Json;
using LittleHelper;

namespace LittleHelper.Tests;

public class JsonRepairTests
{
    [Fact]
    public void Repair_ValidJson_ReturnsSameObject()
    {
        var result = JsonRepair.Repair("{\"path\": \"src/Foo.cs\"}");
        Assert.Equal("src/Foo.cs", result.GetProperty("path").GetString());
    }

    [Fact]
    public void Repair_JsonInCodeFence_ExtractsJson()
    {
        var input = "Here's the call:\n```json\n{\"path\": \"bar.cs\"}\n```\nDone.";
        var result = JsonRepair.Repair(input);
        Assert.Equal("bar.cs", result.GetProperty("path").GetString());
    }

    [Fact]
    public void Repair_JsonInPlainCodeFence_ExtractsJson()
    {
        var input = "```\n{\"command\": \"ls\"}\n```";
        var result = JsonRepair.Repair(input);
        Assert.Equal("ls", result.GetProperty("command").GetString());
    }

    [Fact]
    public void Repair_JsonWithPrefix_ExtractsJsonObject()
    {
        var input = "Sure, here it is:\n{\"path\": \"baz.cs\", \"offset\": 1}";
        var result = JsonRepair.Repair(input);
        Assert.Equal("baz.cs", result.GetProperty("path").GetString());
    }

    [Fact]
    public void Repair_JsonArrayWithPrefix_ExtractsArray()
    {
        var input = "Results: [1, 2, 3]";
        var result = JsonRepair.Repair(input);
        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(3, result.GetArrayLength());
    }

    [Fact]
    public void Repair_CompletelyInvalidJson_ReturnsEmptyObject()
    {
        var result = JsonRepair.Repair("this is not json at all");
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Empty(result.EnumerateObject());
    }

    [Fact]
    public void Repair_EmptyString_ReturnsEmptyObject()
    {
        var result = JsonRepair.Repair("");
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
    }

    [Fact]
    public void LevenshteinDistance_SameStrings_ReturnsZero()
    {
        Assert.Equal(0, JsonRepair.LevenshteinDistance("read", "read"));
    }

    [Fact]
    public void LevenshteinDistance_Transposition_ReturnsTwo()
    {
        // "read" -> "raed" requires delete + insert (transposition = 2 edits for standard Levenshtein)
        Assert.Equal(2, JsonRepair.LevenshteinDistance("read", "raed"));
    }

    [Fact]
    public void LevenshteinDistance_CompletelyDifferent_ReturnsLength()
    {
        Assert.Equal(4, JsonRepair.LevenshteinDistance("read", "wxyz"));
    }

    [Theory]
    [InlineData("reaad", "read", 1)]   // insertion
    [InlineData("serch", "search", 1)]  // insertion
    [InlineData("wite", "write", 1)]     // insertion (w-r-i-t-e vs w-i-t-e: missing 'r')
    [InlineData("writ", "write", 1)]     // deletion
    public void LevenshteinDistance_KnownDistances(string a, string b, int expected)
    {
        Assert.Equal(expected, JsonRepair.LevenshteinDistance(a, b));
    }
}