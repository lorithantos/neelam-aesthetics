using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>The clients table for the stores' and pages' purposes, with its refusals.</summary>
internal sealed class InMemoryClientDirectory(params ClientRecord[] clients) : IClientDirectory
{
    private readonly List<ClientRecord> _clients = [.. clients];

    public Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ClientRecord>>(_clients.ToList());

    public Task AddAsync(ClientRecord client, CancellationToken cancellationToken = default)
    {
        if (_clients.Any(c => c.Name == client.Name || c.GroupId == client.GroupId))
            throw new InvalidOperationException($"{client.Name} or its group is already a client.");
        _clients.Add(client);
        return Task.CompletedTask;
    }
}
