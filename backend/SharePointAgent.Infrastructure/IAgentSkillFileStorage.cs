namespace SharePointAgent.Infrastructure;

public interface IAgentSkillFileStorage
{
    Task UploadAsync(string storageKey, byte[] content, CancellationToken cancellationToken);

    Task<byte[]> ReadAsync(string storageKey, CancellationToken cancellationToken);

    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}
