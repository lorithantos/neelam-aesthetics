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

    /// <summary>A row put in the table by hand, past the rules, as a stored zone this machine does not know would be.</summary>
    public void Put(ClientRecord client)
    {
        lock (_clients) _clients.Add(client);
    }

    public Task AddAsync(ClientRecord client, CancellationToken cancellationToken = default)
    {
        ClientDirectoryRules.CheckRegistration(client);
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

/// <summary>
/// The approvals table: rows by client partition, replaced on write as the table's upsert does. It
/// notes every partition read, so a test can show another client's rows are never asked for.
/// </summary>
internal sealed class InMemoryApprovals : IApprovalStore
{
    private readonly Dictionary<(string Client, Guid Campaign, string Stamp), ApprovalRecord> _rows = [];
    private readonly List<string> _partitionsRead = [];

    public IReadOnlyList<ApprovalRecord> Rows
    {
        get { lock (_rows) return _rows.Values.ToList(); }
    }

    public IReadOnlyList<string> PartitionsRead
    {
        get { lock (_rows) return _partitionsRead.ToList(); }
    }

    public Task<IReadOnlyList<ApprovalRecord>> ForCampaignAsync(
        ClientName client, Guid campaignId, CancellationToken cancellationToken = default)
    {
        lock (_rows)
        {
            _partitionsRead.Add(client.Value);
            return Task.FromResult<IReadOnlyList<ApprovalRecord>>(
                _rows.Values.Where(r => r.Client == client && r.CampaignId == campaignId).ToList());
        }
    }

    public Task PutAsync(ApprovalRecord approval, CancellationToken cancellationToken = default)
    {
        lock (_rows) _rows[(approval.Client.Value, approval.CampaignId, approval.Stamp)] = approval;
        return Task.CompletedTask;
    }

    /// <summary>Writes a row as something outside the store might, such as another client's.</summary>
    public void Seed(ApprovalRecord approval)
    {
        lock (_rows) _rows[(approval.Client.Value, approval.CampaignId, approval.Stamp)] = approval;
    }
}

/// <summary>The activity table: every event written, in order. <see cref="Fail"/> makes every write throw.</summary>
internal sealed class InMemoryActivityLog : IActivityLog
{
    private readonly List<ActivityEvent> _events = [];

    public bool Fail { get; set; }

    public IReadOnlyList<ActivityEvent> Events
    {
        get { lock (_events) return _events.ToList(); }
    }

    public Task RecordAsync(ActivityEvent activity, CancellationToken cancellationToken = default)
    {
        if (Fail) throw new Azure.RequestFailedException(503, "The activity table is unreachable.");
        lock (_events) _events.Add(activity);
        return Task.CompletedTask;
    }
}

/// <summary>A logger that keeps what it is told, to show a failure was logged rather than thrown.</summary>
internal sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly List<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Error)> _entries = [];

    public IReadOnlyList<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Error)> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add((logLevel, formatter(state, exception), exception));
    }
}

/// <summary>
/// Approvals and activity for stores made in a test, as the app wires them: one approvals table and
/// one activity log shared by every store made from it.
/// </summary>
internal sealed class TestRecords(TimeProvider clock)
{
    public InMemoryApprovals Approvals { get; } = new();
    public InMemoryActivityLog Activity { get; } = new();
    public ListLogger<ActivityRecorder> Log { get; } = new();
    public ActivityRecorder Recorder => new(Activity, clock, Log);

    /// <summary>A client's drafts and templates over <paramref name="container"/>, recorded against <paramref name="actor"/>.</summary>
    public CampaignStore Campaigns(
        IBlobBackend container, TimeSpan grace, ClientName? client = null, Actor? actor = null) =>
        new(container, clock, grace, Approvals, Recorder.For(client ?? DefaultClient, actor ?? Actor.Demo));

    public ImageLibrary Images(IBlobBackend container, ClientName? client = null, Actor? actor = null) =>
        new(container, clock, Recorder.For(client ?? DefaultClient, actor ?? Actor.Demo));

    public ClientStores Stores(Func<string, IBlobBackend> containers, TimeSpan grace) =>
        new(containers, clock, grace, Approvals, Recorder);

    public static readonly ClientName DefaultClient = new("test-salon-one");

    /// <summary>A recorder for tests that do not look at the trail: its events go to a log nobody reads.</summary>
    public static ActivityRecorder Unwatched =>
        new(new InMemoryActivityLog(), TimeProvider.System, new ListLogger<ActivityRecorder>());
}

/// <summary>
/// The known items table: one table of rows keyed by partition (the client) and row (the item's id),
/// written and read through the real table's own mapping, with the real store's checks.
/// </summary>
internal sealed class InMemoryKnownItems : IKnownItemStore
{
    private readonly Dictionary<(string Partition, string Row), Azure.Data.Tables.TableEntity> _rows = [];

    /// <summary>Every row in the table, whichever client's.</summary>
    public IReadOnlyList<Azure.Data.Tables.TableEntity> Rows
    {
        get { lock (_rows) return _rows.Values.ToList(); }
    }

    /// <summary>What the table's reading logged: rows it left out.</summary>
    public ListLogger<KnownItemTable> Log { get; } = new();

    /// <summary>Puts a row in as it is, however malformed, as someone editing the table by hand might.</summary>
    public void PutRow(Azure.Data.Tables.TableEntity row)
    {
        lock (_rows) _rows[(row.PartitionKey, row.RowKey)] = row;
    }

    public Task<KnownItems> ForClientAsync(ClientName client, CancellationToken cancellationToken = default)
    {
        lock (_rows)
            return Task.FromResult(KnownItemTable.ReadAll(client, _rows.Values.Where(r => r.PartitionKey == client.Value).ToList(), Log));
    }

    public async Task AddAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default)
    {
        var existing = await ForClientAsync(client, cancellationToken);
        lock (_rows)
        {
            KnownItemStoreRules.CheckAdd(existing, item);
            if (!_rows.TryAdd((client.Value, item.Id), KnownItemTable.FromItem(client, item)))
                throw new InvalidOperationException("That known item already exists.");
        }
    }

    public async Task UpdateAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default)
    {
        var existing = await ForClientAsync(client, cancellationToken);
        lock (_rows)
        {
            KnownItemStoreRules.CheckUpdate(existing, item);
            _rows[(client.Value, item.Id)] = KnownItemTable.FromItem(client, item);
        }
    }

    public Task RemoveAsync(ClientName client, string id, CancellationToken cancellationToken = default)
    {
        lock (_rows) _rows.Remove((client.Value, id));
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
