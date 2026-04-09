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
    public async Task PathEscape_Blocked_ThrowsException()
    {
        var executor = CreateExecutor();
        // Try to read /etc/passwd (outside working directory)
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.Execute("read",
                JsonDocument.Parse("{\"path\": \"../../etc/passwd\"}").RootElement));
    }

    [Fact]
    public async Task PathEscape_SiblingDir_Blocked()
    {
        var executor = CreateExecutor();
        // /tmp/something-evil should not match /tmp/something
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await executor.Execute("read",
                JsonDocument.Parse("{\"path\": \"../something-evil/foo.txt\"}").RootElement));
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
}