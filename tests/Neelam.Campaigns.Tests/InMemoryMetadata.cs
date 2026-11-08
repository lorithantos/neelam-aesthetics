using System.Collections.Concurrent;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>The clients table for the stores' and pages' purposes, with its refusals.</summary>
internal sealed class InMemoryClientDirectory(params ClientRecord[] clients) : IClientDirectory
{
    private readonly List<ClientRecord> _clients = [.. clients];

    public Task<IReadOnlyList<ClientRecord>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_clients) return Task.FromResult<IReadOnlyList<ClientRecord>>([.. _clients]);
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
    private readonly ConcurrentQueue<SupportGrant> _grants = new(grants);

    public Task<IReadOnlyList<SupportGrant>> ForClientAsync(ClientName client, CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<SupportGrant>>([.. _grants.Where(g => g.Client == client)]);
    }

    public Task RecordAsync(SupportGrant grant, CancellationToken cancellationToken = default)
    {
        _grants.Enqueue(grant);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The approvals table: rows by client partition, replaced on write as the table's upsert does. It
/// notes every partition read, so a test can show another client's rows are never asked for.
/// </summary>
internal sealed class InMemoryApprovals : IApprovalStore
{
    private readonly ConcurrentDictionary<(string Client, Guid Campaign, string Stamp), ApprovalRecord> _rows = new();
    private readonly ConcurrentQueue<string> _partitionsRead = new();

    public IReadOnlyList<ApprovalRecord> Rows
    {
        get { return [.. _rows.Values]; }
    }

    public IReadOnlyList<string> PartitionsRead
    {
        get { return [.. _partitionsRead]; }
    }

    public Task<IReadOnlyList<ApprovalRecord>> ForCampaignAsync(
        ClientName client, Guid campaignId, CancellationToken cancellationToken = default)
    {
        _partitionsRead.Enqueue(client.Value);
        return Task.FromResult<IReadOnlyList<ApprovalRecord>>(
            [.. _rows.Values.Where(r => r.Client == client && r.CampaignId == campaignId)]);
    }

    public Task PutAsync(ApprovalRecord approval, CancellationToken cancellationToken = default)
    {
        _rows[(approval.Client.Value, approval.CampaignId, approval.Stamp)] = approval;
        return Task.CompletedTask;
    }

    /// <summary>Writes a row as something outside the store might, such as another client's.</summary>
    public void Seed(ApprovalRecord approval)
    {
        _rows[(approval.Client.Value, approval.CampaignId, approval.Stamp)] = approval;
    }
}

/// <summary>The activity table: every event written, in order. <see cref="Fail"/> makes every write throw.</summary>
internal sealed class InMemoryActivityLog : IActivityLog
{
    private readonly ConcurrentQueue<ActivityEvent> _events = new();

    public bool Fail { get; set; }

    public IReadOnlyList<ActivityEvent> Events
    {
        get { return [.. _events]; }
    }

    public Task RecordAsync(ActivityEvent activity, CancellationToken cancellationToken = default)
    {
        if (Fail) throw new Azure.RequestFailedException(503, "The activity table is unreachable.");
        _events.Enqueue(activity);
        return Task.CompletedTask;
    }
}

/// <summary>A logger that keeps what it is told, to show a failure was logged rather than thrown.</summary>
internal sealed class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    private readonly ConcurrentQueue<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Error)> _entries = new();

    public IReadOnlyList<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Error)> Entries
    {
        get { return [.. _entries]; }
    }

    /// <summary>Raised after each entry, for a test waiting on something logged in the background.</summary>
    public Pulse Logged { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(
        Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        _entries.Enqueue((logLevel, formatter(state, exception), exception));
        this.Logged.Raise();
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
        IBlobBackend container, TimeSpan grace, ClientName? client = null, Actor? actor = null,
        int approvalsRequired = ClientRecord.DefaultApprovalsRequired) =>
        new(container, clock, grace, Approvals, Recorder.For(client ?? DefaultClient, actor ?? Actor.Demo),
            approvalsRequired: approvalsRequired);

    public ImageLibrary Images(IBlobBackend container, ClientName? client = null, Actor? actor = null) =>
        new(container, clock, Recorder.For(client ?? DefaultClient, actor ?? Actor.Demo));

    public ClientStores Stores(Func<string, IBlobBackend> containers, TimeSpan grace) =>
        new(containers, clock, grace, Approvals, Recorder);

    public static readonly ClientName DefaultClient = new("test-salon-one");

    /// <summary>A recorder for tests that do not look at the trail: its events go to a log nobody reads.</summary>
    public static ActivityRecorder Unwatched =>
        new(new InMemoryActivityLog(), new ManualClock(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero)), new ListLogger<ActivityRecorder>());
}

/// <summary>
/// The known items table: one table of rows keyed by partition (the client) and row (the item's id),
/// written and read through the real table's own mapping, with the real store's checks.
/// </summary>
internal sealed class InMemoryKnownItems : IKnownItemStore
{
    private readonly ConcurrentDictionary<(string Partition, string Row), Azure.Data.Tables.TableEntity> _rows = new();

    /// <summary>Every row in the table, whichever client's.</summary>
    public IReadOnlyList<Azure.Data.Tables.TableEntity> Rows
    {
        get { return [.. _rows.Values]; }
    }

    /// <summary>What the table's reading logged: rows it left out.</summary>
    public ListLogger<KnownItemTable> Log { get; } = new();

    /// <summary>Puts a row in as it is, however malformed, as someone editing the table by hand might.</summary>
    public void PutRow(Azure.Data.Tables.TableEntity row)
    {
        _rows[(row.PartitionKey, row.RowKey)] = row;
    }

    /// <summary>A client's rows in the order the real table returns a partition's: by row key, ordinal.</summary>
    public Task<KnownItems> ForClientAsync(ClientName client, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(KnownItemTable.ReadAll(client, [.. _rows.Values.Where(r => r.PartitionKey == client.Value).OrderBy(r => r.RowKey, StringComparer.Ordinal)], Log));
    }

    public async Task AddAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default)
    {
        var existing = await ForClientAsync(client, cancellationToken);
        KnownItemStoreRules.CheckAdd(existing, item);
        if (!_rows.TryAdd((client.Value, item.Id), KnownItemTable.FromItem(client, item)))
            throw new InvalidOperationException("That known item already exists.");
    }

    public async Task UpdateAsync(ClientName client, KnownItem item, CancellationToken cancellationToken = default)
    {
        var existing = await ForClientAsync(client, cancellationToken);
        KnownItemStoreRules.CheckUpdate(existing, item);
        _rows[(client.Value, item.Id)] = KnownItemTable.FromItem(client, item);
    }

    public async Task RemoveAsync(ClientName client, string id, CancellationToken cancellationToken = default)
    {
        var existing = await ForClientAsync(client, cancellationToken);
        KnownItemStoreRules.CheckRemove(existing, id);
        _rows.TryRemove((client.Value, id), out _);
    }
}

/// <summary>
/// The editing tabs table: real <c>TableEntity</c> rows written through the real table's own mapping, each
/// given a <c>Timestamp</c> from the test clock on every write, as the service does.
/// </summary>
internal sealed class InMemoryTabRows(TimeProvider clock) : ITabRows
{
    private readonly ConcurrentDictionary<(string Partition, string Row), Azure.Data.Tables.TableEntity> _rows = new();

    /// <summary>Every row in the table, whichever client's.</summary>
    public IReadOnlyList<Azure.Data.Tables.TableEntity> Rows
    {
        get { return [.. _rows.Values]; }
    }

    /// <summary>Run by a sweep after it has picked its rows and before it removes them, for a test to write in between.</summary>
    public Func<Task>? BeforeSweepRemoves { get; set; }

    public Task WriteAsync(ClientName client, TabSubject subject, Guid tabId, DateTimeOffset? lastTyped,
        CancellationToken cancellationToken = default)
    {
        var row = EditingTabTable.FromTab(client, subject, tabId, lastTyped);
        row.Timestamp = clock.GetUtcNow();
        _rows[(row.PartitionKey, row.RowKey)] = row;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TabRow>> ListAsync(ClientName client, TabSubject subject,
        CancellationToken cancellationToken = default)
    {
        var partition = EditingTabTable.PartitionKey(client, subject);
        return Task.FromResult<IReadOnlyList<TabRow>>([.. _rows.Values
            .Where(r => r.PartitionKey == partition).Select(r => EditingTabTable.ToTab(client, subject, r))]);
    }

    public Task DeleteAsync(ClientName client, TabSubject subject, Guid tabId, CancellationToken cancellationToken = default)
    {
        _rows.TryRemove((EditingTabTable.PartitionKey(client, subject), EditingTabTable.RowKeyOf(tabId)), out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes the rows older than <paramref name="olderThan"/>, each only if it is still the row the sweep
    /// picked, as the service's delete by ETag does: a row written again in between is kept and not counted.
    /// Every write stores a new <c>TableEntity</c>, which compares by reference, so the same instance is the same row.
    /// </summary>
    public async Task<int> SweepAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        var old = _rows.Where(r => r.Value.Timestamp < olderThan).ToList();
        if (BeforeSweepRemoves is { } between) await between();
        return old.Count(row => _rows.TryRemove(row));
    }
}

/// <summary>A storage account's containers, each an <see cref="InMemoryBlobBackend"/> made on first use.</summary>
internal sealed class InMemoryContainers
{
    private readonly ConcurrentDictionary<string, InMemoryBlobBackend> _containers = new(StringComparer.Ordinal);

    public InMemoryBlobBackend For(string container)
    {
        return _containers.GetOrAdd(container, _ => new InMemoryBlobBackend());
    }

    public IReadOnlyCollection<string> Names
    {
        get { return [.. _containers.Keys]; }
    }
}
