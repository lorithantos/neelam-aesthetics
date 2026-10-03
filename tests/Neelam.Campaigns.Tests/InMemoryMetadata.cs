using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>The clients table for the stores' and pages' purposes, with its refusals.</summary>
internal sealed class InMemoryClientDirectory(params ClientRecord[] clients) : IClientDirectory
{
    private readonly List<ClientRecord> _clients = [.. clients];

    public Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_clients) return Task.FromResult<IReadOnlyList<ClientRecord>>(_clients.ToList());
    }

    public Task AddAsync(ClientRecord client, CancellationToken cancellationToken = default)
    {
        lock (_clients)
        {
            if (_clients.Any(c => c.Name == client.Name || c.GroupId == client.GroupId))
                throw new InvalidOperationException($"{client.Name} or its group is already a client.");
            _clients.Add(client);
        }
        return Task.CompletedTask;
    }

    public Task UpdateAsync(ClientRecord client, CancellationToken cancellationToken = default)
    {
        lock (_clients)
        {
            ClientDirectoryRules.CheckUpdate(_clients, client);
            _clients[_clients.FindIndex(c => c.Name == client.Name)] = client;
        }
        return Task.CompletedTask;
    }
}

/// <summary>Support grants, kept after they expire like the table keeps them.</summary>
internal sealed class InMemorySupportGrants(params SupportGrant[] grants) : ISupportGrantStore
{
    private readonly List<SupportGrant> _grants = [.. grants];

    public Task<IReadOnlyList<SupportGrant>> ForClientAsync(ClientName client, CancellationToken cancellationToken = default)
    {
        lock (_grants) return Task.FromResult<IReadOnlyList<SupportGrant>>(_grants.Where(g => g.Client == client).ToList());
    }

    public Task RecordAsync(SupportGrant grant, CancellationToken cancellationToken = default)
    {
        lock (_grants) _grants.Add(grant);
        return Task.CompletedTask;
    }
}

/// <summary>A storage account's containers, each an <see cref="InMemoryBlobBackend"/> made on first use.</summary>
internal sealed class InMemoryContainers
{
    private readonly Dictionary<string, InMemoryBlobBackend> _containers = new(StringComparer.Ordinal);

    public InMemoryBlobBackend For(string container)
    {
        lock (_containers)
        {
            if (!_containers.TryGetValue(container, out var backend))
                _containers[container] = backend = new InMemoryBlobBackend();
            return backend;
        }
    }

    public IReadOnlyCollection<string> Names
    {
        get { lock (_containers) return _containers.Keys.ToList(); }
    }
}
