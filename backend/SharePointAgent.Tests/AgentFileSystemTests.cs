using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

/// <summary>
/// The file tools are only as safe as the path resolution behind them, so most of this is about what
/// the agent must not be able to reach: anything outside the working directory, by any spelling.
/// </summary>
public sealed class AgentFileSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"agent-fs-{Guid.NewGuid():N}");

    private readonly AgentFileSystem _fileSystem;

    public AgentFileSystemTests()
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

    [Theory]
    [InlineData("../escaped.txt")]
    [InlineData("nested/../../escaped.txt")]
    [InlineData("./../../escaped.txt")]
    public void APathLeavingTheWorkingDirectoryIsRefused(string path)
    {
        var error = Assert.Throws<ArgumentException>(() => _fileSystem.Resolve(path));
        Assert.Contains("outside", error.Message);
    }

    [Fact]
    public void AnAbsolutePathIsAcceptedOnlyInsideTheWorkingDirectory()
    {
        // The download tools hand back absolute paths, so those have to keep working.
        var inside = Path.Combine(_root, "item", "report.docx");
        Assert.Equal(Path.GetFullPath(inside), _fileSystem.Resolve(inside));

        Assert.Throws<ArgumentException>(() => _fileSystem.Resolve(Path.Combine(Path.GetTempPath(), "elsewhere.txt")));
        Assert.Throws<ArgumentException>(() => _fileSystem.Resolve(OperatingSystem.IsWindows()
            ? @"C:\Windows\System32\drivers\etc\hosts"
            : "/etc/passwd"));
    }

    [Fact]
    public void ADirectoryWhoseNameMerelyStartsWithTheRootIsNotInsideIt()
    {
        Assert.Throws<ArgumentException>(() => _fileSystem.Resolve(_root + "-sibling/notes.txt"));
    }

    [Fact]
    public async Task WritingCreatesTheDirectoriesItNeedsAndRefusesToClobberByDefault()
    {
        var written = await _fileSystem.WriteTextAsync("notes/summary.md", "# Summary", false, default);
        Assert.Equal("notes/summary.md", written.Path);
        Assert.Equal("# Summary", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "summary.md")));

        var refused = await Assert.ThrowsAsync<ArgumentException>(() =>
            _fileSystem.WriteTextAsync("notes/summary.md", "replaced", false, default));
        Assert.Contains("overwrite", refused.Message);

        await _fileSystem.WriteTextAsync("notes/summary.md", "replaced", true, default);
        Assert.Equal("replaced", await File.ReadAllTextAsync(Path.Combine(_root, "notes", "summary.md")));
    }

    [Fact]
    public async Task ListingReportsPathsTheOtherToolsAccept()
    {
        await _fileSystem.WriteTextAsync("notes/summary.md", "text", false, default);
        await _fileSystem.WriteTextAsync("top.txt", "text", false, default);

        var top = _fileSystem.List(null, recursive: false);
        Assert.Equal(".", top.Path);
        Assert.False(top.Truncated);
        Assert.Equal(["notes", "top.txt"], top.Entries.Select(e => e.Path));
        Assert.True(top.Entries.Single(e => e.Path == "notes").IsDirectory);

        var all = _fileSystem.List(null, recursive: true);
        Assert.Contains("notes/summary.md", all.Entries.Select(e => e.Path));

        // Every path a listing prints goes straight back into another tool.
        foreach (var entry in all.Entries)
        {
            _ = _fileSystem.Resolve(entry.Path, mustExist: true);
        }

        Assert.Throws<ArgumentException>(() => _fileSystem.List("missing", recursive: false));
    }

    [Fact]
    public async Task ListingIsCappedSoOneCallCannotFillTheContextWindow()
    {
        for (var i = 0; i < AgentFileSystem.MaxEntries + 5; i++)
        {
            await _fileSystem.WriteTextAsync($"file-{i}.txt", "x", false, default);
        }

        var listing = _fileSystem.List(null, recursive: false);
        Assert.True(listing.Truncated);
        Assert.Equal(AgentFileSystem.MaxEntries, listing.Entries.Count);
    }

    [Fact]
    public async Task MoveAndCopyTreatAnExistingDirectoryAsADestinationFolder()
    {
        await _fileSystem.WriteTextAsync("report.txt", "text", false, default);
        _fileSystem.CreateDirectory("archive");

        var copied = _fileSystem.Copy("report.txt", "archive", false);
        Assert.Equal("archive/report.txt", copied.Path);
        Assert.True(File.Exists(Path.Combine(_root, "report.txt")));

        var moved = _fileSystem.Move("report.txt", "archive/report-2024.txt", false);
        Assert.Equal("archive/report-2024.txt", moved.Path);
        Assert.False(File.Exists(Path.Combine(_root, "report.txt")));

        Assert.Throws<ArgumentException>(() => _fileSystem.Copy("archive/report.txt", "archive/report-2024.txt", false));
        Assert.Throws<ArgumentException>(() => _fileSystem.Move("archive", "../archive", false));
    }

    [Fact]
    public async Task DeletingNeedsRecursiveForANonEmptyDirectoryAndNeverRemovesTheRoot()
    {
        await _fileSystem.WriteTextAsync("archive/report.txt", "text", false, default);

        var refused = Assert.Throws<ArgumentException>(() => _fileSystem.Delete("archive", false));
        Assert.Contains("recursive", refused.Message);

        _fileSystem.Delete("archive", true);
        Assert.False(Directory.Exists(Path.Combine(_root, "archive")));

        var root = Assert.Throws<ArgumentException>(() => _fileSystem.Delete(".", true));
        Assert.Contains("cannot be deleted", root.Message);
        Assert.True(Directory.Exists(_root));

        Assert.Throws<ArgumentException>(() => _fileSystem.Delete("gone.txt", false));
    }

    [Fact]
    public async Task DownloadsLandInFoldersTheFileToolsCanReach()
    {
        var options = new LocalWorkingDirectoryOptions { Directory = _root };
        var downloads = Path.Combine(Path.GetFullPath(_root), "Downloads");
        Assert.Equal(downloads, options.ResolvedDownloadsDirectory);
        Assert.Equal(Path.Combine(downloads, "SharePoint"), options.ResolvedSharePointDirectory);
        Assert.Equal(Path.Combine(downloads, "Attachments"), options.ResolvedAttachmentsDirectory);

        // The two caches write here. The agent reaches both by a relative path.
        var library = Path.Combine(options.ResolvedSharePointDirectory, "item-1", "report.txt");
        var attachment = Path.Combine(options.ResolvedAttachmentsDirectory, "a1", "original.txt");
        foreach (var file in new[] { library, attachment })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "content");
        }

        Assert.Equal(library, _fileSystem.Resolve("Downloads/SharePoint/item-1/report.txt", mustExist: true));
        Assert.Equal("Downloads/SharePoint/item-1/report.txt", _fileSystem.Relative(library));
        Assert.Equal("Downloads/Attachments/a1/original.txt", _fileSystem.Relative(attachment));

        var listed = _fileSystem.List("Downloads", recursive: true).Entries.Select(e => e.Path).ToArray();
        Assert.Contains("Downloads/SharePoint/item-1/report.txt", listed);
        Assert.Contains("Downloads/Attachments/a1/original.txt", listed);

        // What the agent writes itself sits beside that folder rather than among the downloads.
        await _fileSystem.WriteTextAsync("summary.md", "notes", false, default);
        Assert.Equal(["Downloads", "summary.md"], _fileSystem.List(null, recursive: false).Entries.Select(e => e.Path));
    }

    [Fact]
    public async Task ReadTextOpensWhatTheAgentWroteWithoutADownloadButNothingOutside()
    {
        await _fileSystem.WriteTextAsync("notes.txt", "first\nsecond", false, default);
        var textFiles = new AgentTextFiles(_fileSystem);

        var page = await textFiles.ReadAsync("notes.txt");
        Assert.Equal("first\nsecond", page.Text.TrimEnd('\n'));

        var outside = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outside, "secret");
        try
        {
            await Assert.ThrowsAsync<ArgumentException>(() => textFiles.ReadAsync(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }
}
