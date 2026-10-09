using Microsoft.Extensions.Options;
using SharePointAgent.Application;
using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class AgentTextFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "text-files-" + Guid.NewGuid().ToString("N"));

    public AgentTextFilesTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private AgentFileSystem Workspace => new(Options.Create(new LocalWorkingDirectoryOptions { Directory = _root }));

    [Fact]
    public async Task FilesInTheWorkingDirectoryAreReadByLineRange()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "notes.txt"), "one\r\ntwo\nthree\nfour");
        var files = new AgentTextFiles(Workspace);

        var page = await files.ReadAsync("notes.txt", 2, 3);

        Assert.Equal("two\nthree", page.Text);
        Assert.Equal("notes.txt", page.Path);
        Assert.Equal(4, page.TotalLines);
        Assert.Equal(4, page.NextLine);
        await Assert.ThrowsAsync<ArgumentException>(() => files.ReadAsync("notes.txt", 0));
    }

    [Fact]
    public async Task FilesOutsideTheWorkingDirectoryCannotBeRead()
    {
        var outside = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(outside, "secret");
            var files = new AgentTextFiles(Workspace);

            await Assert.ThrowsAsync<ArgumentException>(() => files.ReadAsync(outside));
            await Assert.ThrowsAsync<ArgumentException>(() => files.ReadAsync("../" + Path.GetFileName(outside)));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task BinaryContentIsRejected()
    {
        await File.WriteAllBytesAsync(Path.Combine(_root, "data.bin"), [0, 1, 2]);

        await Assert.ThrowsAsync<ArgumentException>(() => new AgentTextFiles(Workspace).ReadAsync("data.bin"));
    }
}
