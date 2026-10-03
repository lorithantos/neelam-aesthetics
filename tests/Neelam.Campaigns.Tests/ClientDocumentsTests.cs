using Neelam.Campaigns;
using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// A client's catalog, check policy and look: where each lives, which version is in force, and
/// that the policy in force is the one the checks run with.
/// </summary>
public class ClientDocumentsTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 9, 0, 0, TimeSpan.Zero);
    private static readonly ClientName Neelam = new("neelam-aesthetics");
    private static readonly ClientName Other = new("other-salon");

    private readonly InMemoryBlobBackend _clientContainer = new();
    private readonly InMemoryBlobBackend _settingsContainer = new();
    private readonly ManualClock _clock = new(Start);

    private static readonly ClientCatalog Catalog = new(
    [
        new CatalogEntry("Wellness Injection", OfferingKind.Medication, 45m),
        new CatalogEntry("Hydrafacial", OfferingKind.Procedure, 199m),
    ]);

    [Fact]
    public async Task The_catalog_lives_in_the_client_s_container_and_the_newest_is_in_force()
    {
        var store = ClientStores.CatalogIn(_clientContainer, _clock);
        await store.SaveAsync(ClientCatalog.Empty);
        _clock.Now = Start.AddMinutes(5);
        var newest = await store.SaveAsync(Catalog);

        Assert.Equal("catalog/20261003T090500.0000000Z.json", newest.BlobName);
        Assert.Equal(Catalog.Entries, (await store.CurrentAsync())!.Entries);
        Assert.Equal(2, (await store.HistoryAsync()).Count);
    }

    // A bad change is undone by deleting it, and no record of it is left.
    [Fact]
    public async Task Deleting_the_newest_version_puts_the_previous_back_in_force()
    {
        var store = ClientStores.CatalogIn(_clientContainer, _clock);
        await store.SaveAsync(Catalog);
        _clock.Now = Start.AddMinutes(1);
        var mistake = await store.SaveAsync(ClientCatalog.Empty);

        Assert.True(await store.DeleteAsync(mistake));

        Assert.Equal(Catalog.Entries, (await store.CurrentAsync())!.Entries);
        Assert.Single(_clientContainer.Blobs);
    }

    [Fact]
    public async Task Nothing_saved_means_nothing_in_force()
    {
        Assert.Null(await ClientStores.CatalogIn(_clientContainer, _clock).CurrentAsync());
        Assert.Null(await ClientStores.LookIn(_settingsContainer, Neelam, _clock).CurrentAsync());
    }

    [Fact]
    public async Task Other_blobs_under_a_prefix_are_ignored_not_guessed_at()
    {
        _clientContainer.Put("catalog/readme.txt", "");
        _clientContainer.Put("catalog/old/20261003T090000.0000000Z.json", "{}");
        _clientContainer.Put("drafts/6b1f0c1e9a354c2e8e570d3c9c1a2b44/20261003T090000.0000000Z.json", "{}");

        Assert.Empty(await ClientStores.CatalogIn(_clientContainer, _clock).HistoryAsync());
    }

    // The policy is the client's decision, loosening included, and it is what the checks run with.
    [Fact]
    public async Task The_checks_run_with_the_client_s_own_policy_once_it_has_one()
    {
        var policy = ClientStores.PolicyIn(_clientContainer, _clock);
        var campaign = SampleCampaigns.Corrected();

        var before = CampaignReview.Check(campaign, await ClientStores.PolicyInForceAsync(policy, default));
        Assert.Contains(before.Findings, f => f.Rule == "restricted-term");

        await policy.SaveAsync(CampaignPolicy.Default with
        {
            RestrictedTerms = new Dictionary<string, string> { ["guaranteed"] = "Outcome claims need substantiation." },
        });

        var after = CampaignReview.Check(campaign, await ClientStores.PolicyInForceAsync(policy, default));
        Assert.DoesNotContain(after.Findings, f => f.Rule == "restricted-term");
    }

    [Fact]
    public async Task Until_a_client_saves_a_policy_the_starting_defaults_are_in_force() =>
        Assert.Equal(CampaignPolicy.Default.MaxEmoji,
            (await ClientStores.PolicyInForceAsync(ClientStores.PolicyIn(_clientContainer, _clock), default)).MaxEmoji);

    // Every client's look shares the settings container, each under its own name.
    [Fact]
    public async Task Each_client_s_look_is_kept_apart_in_the_settings_container()
    {
        var neelam = await ClientStores.LookIn(_settingsContainer, Neelam, _clock).SaveAsync(new ClientLook("#B08D57"));
        await ClientStores.LookIn(_settingsContainer, Other, _clock).SaveAsync(new ClientLook("#123456"));

        Assert.StartsWith("neelam-aesthetics/", neelam.BlobName);
        Assert.Equal("#B08D57", (await ClientStores.LookIn(_settingsContainer, Neelam, _clock).CurrentAsync())!.AccentColour);
        Assert.Equal("#123456", (await ClientStores.LookIn(_settingsContainer, Other, _clock).CurrentAsync())!.AccentColour);
    }

    [Fact]
    public async Task A_version_from_another_document_is_refused()
    {
        var catalog = ClientStores.CatalogIn(_clientContainer, _clock);
        var policyVersion = await ClientStores.PolicyIn(_clientContainer, _clock).SaveAsync(CampaignPolicy.Default);

        await Assert.ThrowsAsync<ArgumentException>(() => catalog.LoadAsync(policyVersion));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.DeleteAsync(policyVersion));
    }

    [Fact]
    public void Catalog_policy_and_look_round_trip_as_json()
    {
        Assert.Equal(Catalog.Entries, CampaignJson.DeserializeCatalog(CampaignJson.SerializeCatalog(Catalog)).Entries);

        var policy = CampaignPolicy.Default with { MaxEmoji = 3, MedicalTerms = ["laser"] };
        var back = CampaignJson.DeserializePolicy(CampaignJson.SerializePolicy(policy));
        Assert.Equal(3, back.MaxEmoji);
        Assert.Equal(["laser"], back.MedicalTerms);
        Assert.Equal(policy.RestrictedTerms.OrderBy(t => t.Key), back.RestrictedTerms.OrderBy(t => t.Key));

        Assert.Equal("#B08D57", CampaignJson.DeserializeLook(CampaignJson.SerializeLook(new ClientLook("#B08D57"))).AccentColour);
    }

    [Fact]
    public void A_document_from_another_schema_is_refused() =>
        Assert.Throws<InvalidDataException>(() => CampaignJson.DeserializeCatalog("""{"schema":99,"entries":[]}"""));

    [Fact]
    public void A_catalog_cannot_list_one_thing_twice_or_at_a_negative_price()
    {
        Assert.Throws<ArgumentException>(() => new ClientCatalog(
        [
            new CatalogEntry("Hydrafacial", OfferingKind.Procedure),
            new CatalogEntry("hydrafacial ", OfferingKind.Procedure),
        ]));
        Assert.Throws<ArgumentException>(() => new ClientCatalog([new CatalogEntry("Peel", OfferingKind.Procedure, -1m)]));
        Assert.Throws<ArgumentException>(() => new ClientCatalog([new CatalogEntry(" ", OfferingKind.Procedure)]));
    }

    [Fact]
    public void A_benefit_s_item_name_finds_its_catalog_entry() =>
        Assert.Equal(OfferingKind.Medication, Catalog.Find("wellness injection")!.Kind);

    [Theory]
    [InlineData("B08D57")]
    [InlineData("#B08D5")]
    [InlineData("#GGGGGG")]
    public void A_look_colour_must_be_hex_rrggbb(string colour) =>
        Assert.Throws<ArgumentException>(() => new ClientLook(colour));
}
