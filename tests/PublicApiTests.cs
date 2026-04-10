using System.Text.Json;
using LittleHelper;

namespace LittleHelper.Tests;

public class PublicApiTests
{
    [Fact]
    public void FormatToolDetail_Read_ReturnsPath()
    {
        var args = JsonSerializer.Deserialize<JsonElement>("{\"path\": \"/tmp/test.cs\"}");
        var result = Agent.FormatToolDetail("read", args);
        Assert.Equal("/tmp/test.cs", result);
    }

    [Fact]
    public void FormatToolDetail_Write_ReturnsPath()
    {
        var args = JsonSerializer.Deserialize<JsonElement>("{\"path\": \"src/foo.cs\", \"content\": \"...\"}");
        var result = Agent.FormatToolDetail("write", args);
        Assert.Equal("src/foo.cs", result);
    }

    [Fact]
    public void FormatToolDetail_Run_ReturnsCommand()
    {
        var args = JsonSerializer.Deserialize<JsonElement>("{\"command\": \"dotnet build --verbosity quiet\"}");
        var result = Agent.FormatToolDetail("run", args);
        Assert.Equal("dotnet build --verbosity quiet", result);
    }

    [Fact]
    public void FormatToolDetail_Bash_ReturnsCommand()
    {
        var args = JsonSerializer.Deserialize<JsonElement>("{\"command\": \"ls -la\"}");
        var result = Agent.FormatToolDetail("bash", args);
        Assert.Equal("ls -la", result);
    }

    [Fact]
    public void FormatToolDetail_Search_ReturnsPattern()
    {
        var args = JsonSerializer.Deserialize<JsonElement>("{\"pattern\": \"TODO\"}");
        var result = Agent.FormatToolDetail("search", args);
        Assert.Equal("\"TODO\"", result);
    }

    [Fact]
    public void FormatToolDetail_Unknown_ReturnsRawTruncated()
    {
        var args = JsonSerializer.Deserialize<JsonElement>("{\"foo\": \"bar\", \"baz\": 42}");
        var result = Agent.FormatToolDetail("custom", args);
        Assert.NotEmpty(result);
    }

    [Fact]
    public void SessionLogReader_ReadEntries_NonexistentFile_ReturnsEmpty()
    {
        var entries = SessionLogReader.ReadEntries("/nonexistent/path.jsonl");
        Assert.Empty(entries);
    }

    [Fact]
    public void SessionLogReader_ReadEntries_ParsesValidJsonl()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tempFile, new[]
            {
                "{\"type\":\"session_start\",\"timestamp\":\"2026-01-01T00:00:00Z\",\"model\":\"test\",\"working_dir\":\"/tmp\"}",
                "{\"type\":\"step\",\"step\":1,\"tokens\":100,\"thinking_tokens\":10,\"tool_calls\":2}",
                "{\"type\":\"session_end\",\"success\":true,\"steps\":5,\"total_tokens\":500}"
            });

            var entries = SessionLogReader.ReadEntries(tempFile);
            Assert.Equal(3, entries.Count);
            Assert.Equal("session_start", entries[0].Type);
            Assert.Equal("test", entries[0].Model);
            Assert.Equal("step", entries[1].Type);
            Assert.Equal(1, entries[1].Step);
            Assert.Equal(100, entries[1].Tokens);
            Assert.Equal("session_end", entries[2].Type);
            Assert.True(entries[2].Success);
            Assert.Equal(500, entries[2].TotalTokens);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SessionLogReader_ReadEntries_SkipsMalformedLines()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllLines(tempFile, new[]
            {
                "{\"type\":\"session_start\",\"model\":\"test\"}",
                "this is not json",
                "",
                "{\"type\":\"step\",\"step\":1,\"tokens\":50}"
            });

            var entries = SessionLogReader.ReadEntries(tempFile);
            Assert.Equal(2, entries.Count);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
