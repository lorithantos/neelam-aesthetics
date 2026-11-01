using System.Text.Json;
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
            ClientStores.SettingsContainer + "/",
        };
        var spec = Spec();
        var seen = new List<string>();

        foreach (var container in spec.GetProperty("containers").EnumerateArray())
        {
            foreach (var rule in container.GetProperty("rules").EnumerateArray())
            {
                var match = rule.GetProperty("match").GetString()!;
                seen.Add(LeadingLiteral(match));
                Assert.Contains(LeadingLiteral(match), known);

                if (rule.TryGetProperty("dropIfMetadata", out var mark))
                    Assert.Equal(UndoMark.Key, mark.GetString());
                if (rule.TryGetProperty("keepIfLive", out var live))
                    Assert.Contains(LeadingLiteral(live.GetString()!), known);
            }
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
        Assert.Equal(ClientStores.SettingsContainer + "/{client}/", containers[1].GetProperty("prefix").GetString());
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
