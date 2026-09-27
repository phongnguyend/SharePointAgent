using SharePointAgent.Infrastructure;
using Xunit;

namespace SharePointAgent.Tests;

public sealed class AgentTextFilesTests
{
    [Fact]
    public async Task OnlyRegisteredPathsCanBeReadAndGrantsAreTurnScoped()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "one\r\ntwo\nthree\nfour");
            var files = new AgentTextFiles();
            await Assert.ThrowsAsync<ArgumentException>(() => files.ReadAsync(path));
            files.Register(path);
            var page = await files.ReadAsync(path, 2, 3);
            Assert.Equal("two\nthree", page.Text);
            Assert.Equal(4, page.TotalLines);
            Assert.Equal(4, page.NextLine);
            await Assert.ThrowsAsync<ArgumentException>(() => new AgentTextFiles().ReadAsync(path));
            await Assert.ThrowsAsync<ArgumentException>(() => files.ReadAsync(path, 0));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task BinaryContentIsRejected()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, [0, 1, 2]);
            var files = new AgentTextFiles();
            files.Register(path);
            await Assert.ThrowsAsync<ArgumentException>(() => files.ReadAsync(path));
        }
        finally { File.Delete(path); }
    }
}
