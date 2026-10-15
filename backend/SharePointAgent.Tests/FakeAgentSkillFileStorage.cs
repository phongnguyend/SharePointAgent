using SharePointAgent.Infrastructure;

namespace SharePointAgent.Tests;

internal sealed class FakeAgentSkillFileStorage : IAgentSkillFileStorage
{
    public Dictionary<string, byte[]> Files { get; } = [];

    public int ReadCount { get; private set; }

    public int UploadCount { get; private set; }

    public int? FailOnUpload { get; set; }

    public int? FailOnRead { get; set; }

    public Task UploadAsync(string storageKey, byte[] content, CancellationToken cancellationToken)
    {
        UploadCount++;
        if (UploadCount == FailOnUpload)
        {
            throw new IOException("Storage upload failed.");
        }
        Files.Add(storageKey, content);
        return Task.CompletedTask;
    }

    public Task<byte[]> ReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        ReadCount++;
        if (ReadCount == FailOnRead)
        {
            throw new IOException("Storage download failed.");
        }
        return Task.FromResult(Files[storageKey]);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        Files.Remove(storageKey);
        return Task.CompletedTask;
    }
}
