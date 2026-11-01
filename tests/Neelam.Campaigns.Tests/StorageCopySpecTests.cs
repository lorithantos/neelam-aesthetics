using System.Text.Json;
using System.Text.RegularExpressions;
using Neelam.Campaigns.Storage;
using Xunit;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// <c>infra/storage-copy.json</c> tells Janet's storage copy which of this app's blobs and table rows
/// to copy from one test account to the other. Janet knows nothing about Neelam, so the file is the
/// only place the layout is written down outside the code. These tests hold it to the constants the
/// store writes with, so the two cannot drift apart. No test touches the network.
/// </summary>
public class StorageCopySpecTests
{
    // The folders DocumentKind maps to at CampaignStore.Prefix, which is private; the rest come from constants.
    private const string DraftFolder = "drafts/";
    private const string TemplateFolder = "templates/";

    private static JsonElement Spec()
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "storage-copy.json"));
        return JsonDocument.Parse(file).RootElement.Clone();
    }

    private static string LeadingLiteral(string pattern) => pattern[..pattern.IndexOf('{')];

    [Fact]
    public void EveryRulePrefixIsOneTheStoreWrites()
    {
        var known = new HashSet<string>
        {
            DraftFolder,
            TemplateFolder,
            CampaignStore.ProofreadPrefix,
            PhotoReadingStore.Prefix,
            ClientStores.CatalogPrefix + "/",
            ClientStores.PolicyPrefix + "/",
            ClientStores.BaselinePrefix + "/",
            ClientStores.LaddersPrefix + "/",
            ImageLibrary.Prefix,
        };
        var spec = Spec();
        var seen = new List<string>();

        // The client's own container; the settings container is laid out differently and has its own test.
        foreach (var rule in spec.GetProperty("containers")[0].GetProperty("rules").EnumerateArray())
        {
            var match = rule.GetProperty("match").GetString()!;
            seen.Add(LeadingLiteral(match));
            Assert.Contains(LeadingLiteral(match), known);

            if (rule.TryGetProperty("dropIfMetadata", out var mark))
                Assert.Equal(UndoMark.Key, mark.GetString());
            if (rule.TryGetProperty("keepIfLive", out var live))
                Assert.Contains(LeadingLiteral(live.GetString()!), known);
        }

        // A rule dropped from the file would otherwise pass: every prefix the store writes is covered.
        Assert.Equal(known.OrderBy(p => p), seen.Distinct().OrderBy(p => p));
    }

    [Fact]
    public void TheTablesAndTheSettingsContainerAreTheOnesTheStoreUses()
    {
        var spec = Spec();

        var tables = spec.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("name").GetString());
        Assert.Equal(
            new[] { KnownItemTable.Table, TableMetadata.ApprovalsTable }.OrderBy(n => n),
            tables.OrderBy(n => n));

        var containers = spec.GetProperty("containers").EnumerateArray().ToList();
        Assert.Equal(2, containers.Count);
        Assert.Equal("{client}", containers[0].GetProperty("name").GetString());
        Assert.Equal(ClientStores.SettingsContainer, containers[1].GetProperty("name").GetString());
        Assert.Equal("{client}/", containers[1].GetProperty("prefix").GetString());
    }

    // Inside the settings container a client's look is "{client}/{stamp}.json", not "settings/{client}/...".
    // Pinned to what ClientStores really writes, not to a string typed here.
    [Fact]
    public async Task TheSettingsRuleMatchesTheBlobTheStoreActuallySavesALookAs()
    {
        var client = new ClientName("test-salon-one");
        var settings = new InMemoryBlobBackend();
        var saved = await ClientStores.LookIn(settings, client, new ManualClock(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero)))
            .SaveAsync(new ClientLook("#B08D57"));

        var container = Spec().GetProperty("containers")[1];
        string Expand(string pattern) => Regex.Replace(
            Regex.Escape(pattern).Replace(@"\{client}", Regex.Escape(client.Value)),
            @"\\\{[a-z]+}", "[^/]+");
        var match = container.GetProperty("rules")[0].GetProperty("match").GetString()!;
        var prefix = container.GetProperty("prefix").GetString()!.Replace("{client}", client.Value);

        Assert.Matches("^" + Expand(match) + "$", saved.BlobName);
        Assert.StartsWith(prefix, saved.BlobName);
        Assert.Contains(saved.BlobName, settings.Blobs.Keys);
    }

    [Fact]
    public void HerAccountIsOnlyEverASource()
    {
        var pairs = Spec().GetProperty("pairs").EnumerateArray().ToList();
        Assert.NotEmpty(pairs);

        var sources = pairs.Select(p => p.GetProperty("source").GetProperty("account").GetString()).ToHashSet();
        foreach (var pair in pairs)
            Assert.DoesNotContain(pair.GetProperty("target").GetProperty("account").GetString(), sources);
    }
}
