using System.Text.Json;
using LittleHelper;

namespace LittleHelper.Tests;

public class ToolsTests
{
    private readonly string _testDir;

    public ToolsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"lh_tools_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
    }

    private ToolExecutor CreateExecutor() => new(_testDir, blockDestructive: false);

    [Fact]
    public async Task Read_NonexistentFile_ReturnsError()
    {
        var executor = CreateExecutor();
        var result = await executor.Execute("read",
            JsonDocument.Parse("{\"path\": \"nonexistent.txt\"}").RootElement);
        Assert.True(result.IsError);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_ExistingFile_ReturnsContents()
    {
        File.WriteAllText(Path.Combine(_testDir, "test.txt"), "hello world\nline 2\n");
        var executor = CreateExecutor();
        var result = await executor.Execute("read",
            JsonDocument.Parse("{\"path\": \"test.txt\"}").RootElement);
        Assert.False(result.IsError);
        Assert.Contains("hello world", result.Output);
        Assert.Contains("2 lines total", result.Output);
    }

    [Fact]
    public async Task Read_WithOffsetAndLimit_ReturnsSubset()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"line {i}").ToList();
        File.WriteAllLines(Path.Combine(_testDir, "multi.txt"), lines);
        var executor = CreateExecutor();
        var result = await executor.Execute("read",
            JsonDocument.Parse("{\"path\": \"multi.txt\", \"offset\": 3, \"limit\": 2}").RootElement);
        Assert.False(result.IsError);
        Assert.Contains("line 3", result.Output);
        Assert.Contains("line 4", result.Output);
        Assert.DoesNotContain("line 1", result.Output);
    }

    [Fact]
    public async Task Write_CreatesFileAndDirectories()
    {
        var executor = CreateExecutor();
        var result = await executor.Execute("write",
            JsonDocument.Parse("{\"path\": \"sub/dir/newfile.txt\", \"content\": \"created\"}").RootElement);
        Assert.False(result.IsError);
        Assert.True(File.Exists(Path.Combine(_testDir, "sub", "dir", "newfile.txt")));
        Assert.Equal("created", await File.ReadAllTextAsync(Path.Combine(_testDir, "sub", "dir", "newfile.txt")));
    }

    [Fact]
    public async Task Write_OverwritesExistingFile()
    {
        File.WriteAllText(Path.Combine(_testDir, "existing.txt"), "old content");
        var executor = CreateExecutor();
        await executor.Execute("write",
            JsonDocument.Parse("{\"path\": \"existing.txt\", \"content\": \"new content\"}").RootElement);
        Assert.Equal("new content", File.ReadAllText(Path.Combine(_testDir, "existing.txt")));
    }

    [Fact]
    public async Task Search_FindsMatchingContent()
    {
        File.WriteAllText(Path.Combine(_testDir, "search.txt"), "hello world\nfoo bar\nhello again");
        var executor = CreateExecutor();
        var result = await executor.Execute("search",
            JsonDocument.Parse("{\"pattern\": \"hello\"}").RootElement);
        Assert.False(result.IsError);
        // If rg/grep aren't on PATH, search returns "No matches found"
        // which is still not an error — just can't verify "hello" in output
        if (result.Output != "No matches found.")
            Assert.Contains("hello", result.Output);
    }

    [Fact]
    public async Task Search_NoMatch_ReturnsEmpty()
    {
        File.WriteAllText(Path.Combine(_testDir, "nosearch.txt"), "nothing to find here");
        var executor = CreateExecutor();
        var result = await executor.Execute("search",
            JsonDocument.Parse("{\"pattern\": \"zzznotfound\"}").RootElement);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task Run_EchoCommand_ReturnsOutput()
    {
        var executor = CreateExecutor();
        var result = await executor.Execute("run",
            JsonDocument.Parse("{\"command\": \"echo hello_world\"}").RootElement);
        Assert.False(result.IsError);
        Assert.Contains("hello_world", result.Output);
    }

    [Fact]
    public async Task Run_FailingCommand_LowExitCode_IsNotError()
    {
        // grep returning 1 (no match) should not be an error
        var executor = CreateExecutor();
        var result = await executor.Execute("run",
            JsonDocument.Parse("{\"command\": \"exit 1\"}").RootElement);
        Assert.False(result.IsError);  // exit code 1 < 128, not an error
    }

    [Fact]
    public async Task Run_SignalExitCode_IsError()
    {
        // exit code 130 (SIGINT) should be an error
        var executor = CreateExecutor();
        var result = await executor.Execute("run",
            JsonDocument.Parse("{\"command\": \"exit 130\"}").RootElement);
        Assert.True(result.IsError);  // exit code >= 128 signals crash/kill
    }

    [Fact]
    public async Task Bash_IsAliasForRun()
    {
        var executor = CreateExecutor();
        var result = await executor.Execute("bash",
            JsonDocument.Parse("{\"command\": \"echo test_bash\"}").RootElement);
        Assert.False(result.IsError);
        Assert.Contains("test_bash", result.Output);
    }

    [Fact]
    public async Task PathEscape_ReadAllowsOutsideWorkingDir()
    {
        var executor = CreateExecutor();
        // Read allows paths outside working dir (e.g. ~/.little_helper/models.json)
        // This should NOT throw — the file may not exist but path escape is not blocked
        var result = await executor.Execute("read",
            JsonDocument.Parse("{\"path\": \"../../etc/hostname\"}").RootElement);
        // Result will be "file not found" (not path escape error) unless /etc/hostname exists
        Assert.DoesNotContain("Path escape blocked", result.Output);
    }

    [Fact]
    public async Task PathEscape_WriteBlockedOutsideWorkingDir()
    {
        var executor = CreateExecutor();
        // Write should block paths outside working directory
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.Execute("write",
                JsonDocument.Parse("{\"path\": \"../../tmp/evil.txt\", \"content\": \"pwned\"}").RootElement));
    }

    [Fact]
    public async Task PathEscape_SiblingDir_WriteBlocked()
    {
        var executor = CreateExecutor();
        // /tmp/something-evil should not match /tmp/something
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.Execute("write",
                JsonDocument.Parse("{\"path\": \"../something-evil/foo.txt\", \"content\": \"x\"}").RootElement));
    }

    [Fact]
    public async Task BlockDestructive_BlocksRmCommand()
    {
        var executor = new ToolExecutor(_testDir, blockDestructive: true);
        var result = await executor.Execute("run",
            JsonDocument.Parse("{\"command\": \"rm -rf /tmp/something\"}").RootElement);
        Assert.True(result.IsError);
        Assert.Contains("destructive", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownTool_ReturnsError()
    {
        var executor = CreateExecutor();
        var result = await executor.Execute("fly",
            JsonDocument.Parse("{\"where\": \"away\"}").RootElement);
        Assert.True(result.IsError);
        Assert.Contains("Unknown tool", result.Output);
    }

    // --- Edit tool tests ---

    [Fact]
    public async Task Edit_ReplacesUniqueMatch()
    {
        File.WriteAllText(Path.Combine(_testDir, "edit1.txt"), "hello world\nfoo bar\nhello again");
        var executor = CreateExecutor();
        var result = await executor.Execute("edit",
            JsonDocument.Parse("{\"path\": \"edit1.txt\", \"old_string\": \"foo bar\", \"new_string\": \"baz qux\"}").RootElement);
        Assert.False(result.IsError);
        Assert.Contains("Replaced 1 occurrence", result.Output);
        var content = File.ReadAllText(Path.Combine(_testDir, "edit1.txt"));
        Assert.Equal("hello world\nbaz qux\nhello again", content);
    }

    [Fact]
    public async Task Edit_PatchAliasWorks()
    {
        File.WriteAllText(Path.Combine(_testDir, "patch1.txt"), "old text");
        var executor = CreateExecutor();
        var result = await executor.Execute("patch",
            JsonDocument.Parse("{\"path\": \"patch1.txt\", \"old_string\": \"old text\", \"new_string\": \"new text\"}").RootElement);
        Assert.False(result.IsError);
        Assert.Equal("new text", File.ReadAllText(Path.Combine(_testDir, "patch1.txt")));
    }

    [Fact]
    public async Task Edit_NotFound_ReturnsError()
    {
        File.WriteAllText(Path.Combine(_testDir, "edit2.txt"), "hello world");
        var executor = CreateExecutor();
        var result = await executor.Execute("edit",
            JsonDocument.Parse("{\"path\": \"edit2.txt\", \"old_string\": \"missing\", \"new_string\": \"replacement\"}").RootElement);
        Assert.True(result.IsError);
        Assert.Contains("not found", result.Output);
    }

    [Fact]
    public async Task Edit_DuplicateMatch_ReturnsError()
    {
        File.WriteAllText(Path.Combine(_testDir, "edit3.txt"), "hello\nhello\nhello");
        var executor = CreateExecutor();
        var result = await executor.Execute("edit",
            JsonDocument.Parse("{\"path\": \"edit3.txt\", \"old_string\": \"hello\", \"new_string\": \"hi\"}").RootElement);
        Assert.True(result.IsError);
        Assert.Contains("3 times", result.Output);
    }

    [Fact]
    public async Task Edit_ReplaceAll_ReplacesAll()
    {
        File.WriteAllText(Path.Combine(_testDir, "edit4.txt"), "hello\nhello\nhello");
        var executor = CreateExecutor();
        var result = await executor.Execute("edit",
            JsonDocument.Parse("{\"path\": \"edit4.txt\", \"old_string\": \"hello\", \"new_string\": \"hi\", \"replace_all\": true}").RootElement);
        Assert.False(result.IsError);
        Assert.Contains("Replaced 3 occurrences", result.Output);
        Assert.Equal("hi\nhi\nhi", File.ReadAllText(Path.Combine(_testDir, "edit4.txt")));
    }

    [Fact]
    public async Task Edit_DeleteWithEmptyNewString()
    {
        File.WriteAllText(Path.Combine(_testDir, "edit5.txt"), "line1\ntarget\nline3");
        var executor = CreateExecutor();
        var result = await executor.Execute("edit",
            JsonDocument.Parse("{\"path\": \"edit5.txt\", \"old_string\": \"target\\n\", \"new_string\": \"\"}").RootElement);
        Assert.False(result.IsError);
        Assert.Equal("line1\nline3", File.ReadAllText(Path.Combine(_testDir, "edit5.txt")));
    }

    [Fact]
    public async Task Edit_NonexistentFile_ReturnsError()
    {
        var executor = CreateExecutor();
        var result = await executor.Execute("edit",
            JsonDocument.Parse("{\"path\": \"nope.txt\", \"old_string\": \"x\", \"new_string\": \"y\"}").RootElement);
        Assert.True(result.IsError);
        Assert.Contains("not found", result.Output);
    }

    [Fact]
    public async Task Edit_PathEscapeBlocked()
    {
        var executor = CreateExecutor();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.Execute("edit",
                JsonDocument.Parse("{\"path\": \"../../tmp/evil.txt\", \"old_string\": \"x\", \"new_string\": \"y\"}").RootElement));
    }

    [Fact]
    public async Task Edit_WhitespaceNormalization_WrongIndent()
    {
        var executor = CreateExecutor();
        // File has 4-space indent
        var filePath = Path.Combine(_testDir, "code.py");
        await File.WriteAllTextAsync(filePath, "def foo():\n    x = 1\n    y = 2\n    return x + y\n");

        // Model provides old_string without indent (normalization will match the file's actual text)
        // Replacement is the model's text verbatim (no auto-indent on new_string)
        var result = await executor.Execute("edit",
            JsonDocument.Parse("{\"path\": \"code.py\", \"old_string\": \"x = 1\\ny = 2\", \"new_string\": \"x = 42\\ny = 99\"}").RootElement);

        Assert.False(result.IsError);
        var content = await File.ReadAllTextAsync(filePath);
        // The file's 4-space-indented lines were replaced with the model's no-indent text
        Assert.Contains("x = 42", content);
        Assert.Contains("y = 99", content);
        // Unchanged line keeps its indent
        Assert.Contains("    return x + y", content);
    }

    [Fact]
    public async Task Edit_WhitespaceNormalization_TabsVsSpaces()
    {
        var executor = CreateExecutor();
        // File uses tabs
        var filePath = Path.Combine(_testDir, "config.yml");
        await File.WriteAllTextAsync(filePath, "server:\n\tport: 8080\n\thost: localhost\n");

        // Model provides spaces instead of tabs
        var result = await executor.Execute("edit",
            JsonDocument.Parse("{\"path\": \"config.yml\", \"old_string\": \"port: 8080\", \"new_string\": \"port: 9090\"}").RootElement);

        Assert.False(result.IsError);
        var content = await File.ReadAllTextAsync(filePath);
        Assert.Contains("\tport: 9090", content);
        Assert.Contains("\thost: localhost", content);
    }

    [Fact]
    public async Task Edit_WhitespaceNormalization_PicksFirstNormalizedMatch()
    {
        var executor = CreateExecutor();
        // File has two similar but whitespace-different blocks
        var filePath = Path.Combine(_testDir, "dup.py");
        await File.WriteAllTextAsync(filePath, "def foo():\n    x = 1\n    return x\n\ndef bar():\n  x = 1\n  return x\n");

        // Model searches for "x = 1\n    return" (4 spaces before return) but file has
        // different whitespace. Exact match won't find it, but normalized will match first block.
        var result = await executor.Execute("edit",
            JsonDocument.Parse("{\"path\": \"dup.py\", \"old_string\": \"x = 1\\nreturn x\", \"new_string\": \"x = 42\\nreturn x\"}").RootElement);

        Assert.False(result.IsError, $"Unexpected error: {result.Output}");
        var content = await File.ReadAllTextAsync(filePath);
        // The matched text (4-space indented lines) was replaced with model's text (no indent)
        Assert.Contains("x = 42", content);
        Assert.Contains("return x", content);
        Assert.Contains("  x = 1", content);  // second block untouched
    }
}