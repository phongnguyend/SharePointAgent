namespace SharePointAgent.Infrastructure;

public sealed class AgentSkillLeaseSet : IDisposable
{
    private readonly List<AgentSkillFileLease> _leases = [];

    public string[] Paths => _leases.Select(x => x.Path).ToArray();

    public string[] Names => _leases.Select(x => x.Name).ToArray();

    internal void Add(AgentSkillFileLease lease) => _leases.Add(lease);

    public void Dispose()
    {
        foreach (var lease in _leases)
        {
            lease.Dispose();
        }
    }
}
