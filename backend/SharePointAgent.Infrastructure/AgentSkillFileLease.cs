namespace SharePointAgent.Infrastructure;

public sealed class AgentSkillFileLease(string path, string name, Action release) : IDisposable
{
    private Action? _release = release;

    public string Path { get; } = path;

    public string Name { get; } = name;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
