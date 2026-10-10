using System.IO.Compression;
using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Domain;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

/// <summary>
/// Zip and unzip on the local working directory, and which tools the agent is offered. An archive is input
/// the agent did not write, so most of this is about entries that try to land somewhere they should not.
/// </summary>
public sealed class WorkspaceArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"agent-zip-{Guid.NewGuid():N}");

    private readonly AgentFileSystem _fileSystem;

    public WorkspaceArchiveTests()
    {
        Directory.CreateDirectory(_root);
        _fileSystem = new AgentFileSystem(Options.Create(new LocalWorkingDirectoryOptions { Directory = _root }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    [Fact]
    public async Task ZippingAndUnzippingRestoresTheSameLayout()
    {
        await _fileSystem.WriteTextAsync("report/summary.md", "# Summary", overwrite: false, default);
        await _fileSystem.WriteTextAsync("report/data/figures.csv", "a,b", overwrite: false, default);

        var archive = await _fileSystem.ZipAsync(["report"], "out/report.zip", overwrite: false, default);
        var extracted = await _fileSystem.UnzipAsync("out/report.zip", "restored", overwrite: false, default);

        Assert.Equal("out/report.zip", archive.Path);
        Assert.Equal("restored", extracted.Path);
        Assert.Equal("# Summary", await File.ReadAllTextAsync(Path.Combine(_root, "restored", "report", "summary.md")));
        Assert.Equal("a,b", await File.ReadAllTextAsync(Path.Combine(_root, "restored", "report", "data", "figures.csv")));
    }

    [Fact]
    public async Task AnArchiveWrittenInsideTheZippedDirectoryDoesNotIncludeItself()
    {
        await _fileSystem.WriteTextAsync("work/a.txt", "a", overwrite: false, default);

        await _fileSystem.ZipAsync(["work"], "work/work.zip", overwrite: false, default);

        using var archive = ZipFile.OpenRead(Path.Combine(_root, "work", "work.zip"));
        Assert.Equal(["work/a.txt"], archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')));
    }

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("nested/../../escaped.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData(".agent-workspace")]
    [InlineData("nested/.agent-snapshot.zip")]
    public async Task AnArchiveWithAnEscapingOrReservedEntryIsRefusedWhole(string entryName)
    {
        CreateArchive("bad.zip", ("fine.txt", "fine"), (entryName, "bad"));

        await Assert.ThrowsAsync<ArgumentException>(() => _fileSystem.UnzipAsync("bad.zip", "out", overwrite: false, default));

        Assert.False(File.Exists(Path.Combine(_root, "out", "fine.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "escaped.txt")));
    }

    [Fact]
    public async Task AnExistingFileIsReplacedOnlyWithOverwrite()
    {
        await _fileSystem.WriteTextAsync("out/a.txt", "old", overwrite: false, default);
        CreateArchive("new.zip", ("a.txt", "new"), ("b.txt", "b"));

        var clash = await Assert.ThrowsAsync<ArgumentException>(() => _fileSystem.UnzipAsync("new.zip", "out", overwrite: false, default));
        Assert.Contains("already exists", clash.Message);
        Assert.False(File.Exists(Path.Combine(_root, "out", "b.txt")));

        await _fileSystem.UnzipAsync("new.zip", "out", overwrite: true, default);
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(_root, "out", "a.txt")));
    }

    [Fact]
    public async Task AFileThatIsNotAZipIsReportedAsSuch()
    {
        await _fileSystem.WriteTextAsync("notes.zip", "not an archive", overwrite: false, default);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => _fileSystem.UnzipAsync("notes.zip", "out", overwrite: false, default));

        Assert.Contains("not a valid zip file", error.Message);
    }

    [Fact]
    public async Task PathsOutsideTheWorkingDirectoryCannotBeZipped()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _fileSystem.ZipAsync(["../"], "all.zip", overwrite: false, default));
        await Assert.ThrowsAsync<ArgumentException>(() => _fileSystem.ZipAsync(["."], "../outside.zip", overwrite: false, default));
    }

    [Fact]
    public void ScriptExecutionIsListedOnlyForAnIsolatedWorkspace()
    {
        Assert.Contains(ChatAgentService.GetTools(), tool => tool.Name == ChatAgentToolNames.ZipFiles);
        Assert.Contains(ChatAgentService.GetTools(), tool => tool.Name == ChatAgentToolNames.UnzipFile);
        Assert.DoesNotContain(ChatAgentService.GetTools(), tool => tool.Name == ChatAgentToolNames.ExecuteScript);
        Assert.Contains(ChatAgentService.GetTools(includeScriptExecution: true), tool => tool.Name == ChatAgentToolNames.ExecuteScript);
    }

    [Fact]
    public async Task TheLocalWorkingDirectoryRunsNoCode()
    {
        IAgentWorkspace workspace = _fileSystem;

        Assert.False(workspace.IsIsolated);
        await Assert.ThrowsAsync<NotSupportedException>(() => workspace.ExecuteAsync(
            new WorkspaceExecution("python", "print(1)", null, [], null, 10), default));
    }

    private void CreateArchive(string name, params (string Entry, string Content)[] entries)
    {
        using var archive = ZipFile.Open(Path.Combine(_root, name), ZipArchiveMode.Create);
        foreach (var (entryName, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryName).Open());
            writer.Write(content);
        }
    }
}
