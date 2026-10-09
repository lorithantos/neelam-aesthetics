using Neelam.Campaigns.Storage;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The template baseline: the parts every template should have, held as data. The operator's
/// standard applies until a client saves its own; a missing part warns and never stops a save.
/// </summary>
public class TemplateBaselineTests
{
    private static readonly ClientName Neelam = new("neelam-aesthetics");
    private static readonly ClientName Other = new("other-salon");

    private readonly InMemoryContainers _containers = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));

    private TestRecords? _records;
    private TestRecords Records => _records ??= new(_clock);
    private ClientStores Stores => Records.Stores(_containers.For, TimeSpan.FromDays(1));

    private static CampaignTemplate Template(params TemplateBlock[] blocks) => new("Closed Monday", blocks);

    [Fact]
    public async Task The_standard_baseline_applies_when_the_client_has_none_of_its_own()
    {
        var inForce = await Stores.BaselineInForceAsync(Neelam);

        Assert.False(inForce.IsOwn);
        Assert.Equal(
            [BlockType.Header, BlockType.Heading, BlockType.SignOff, BlockType.Button, BlockType.Image],
            inForce.Baseline.Parts);
    }

    // The standard is data the operator can change, in operator settings, not in the code.
    [Fact]
    public async Task The_operator_s_saved_standard_replaces_the_starting_one_for_every_client()
    {
        var saved = await Stores.StandardBaseline().SaveAsync(new TemplateBaseline([BlockType.Heading]));

        Assert.StartsWith("_standard-baseline/", saved.BlobName);
        Assert.Contains(saved.BlobName, _containers.For("settings").Blobs.Keys);
        var inForce = await Stores.BaselineInForceAsync(Neelam);
        Assert.False(inForce.IsOwn);
        Assert.Equal([BlockType.Heading], inForce.Baseline.Parts);
    }

    [Fact]
    public async Task The_client_s_own_baseline_overrides_the_standard_and_lives_in_its_container()
    {
        await Stores.StandardBaseline().SaveAsync(new TemplateBaseline([BlockType.Heading]));
        var saved = await Stores.Baseline(Neelam).SaveAsync(new TemplateBaseline([BlockType.Greeting, BlockType.Offer]));

        var inForce = await Stores.BaselineInForceAsync(Neelam);

        Assert.True(inForce.IsOwn);
        Assert.Equal([BlockType.Greeting, BlockType.Offer], inForce.Baseline.Parts);
        Assert.StartsWith("baseline/", saved.BlobName);
        Assert.Contains(saved.BlobName, _containers.For(Neelam.Value).Blobs.Keys);
    }

    // Every version goes, not just the newest: otherwise an older own baseline would come back.
    [Fact]
    public async Task Using_the_standard_baseline_deletes_the_client_s_own_and_the_standard_applies_again()
    {
        await Stores.Baseline(Neelam).SaveAsync(new TemplateBaseline([BlockType.Greeting]));
        _clock.Now += TimeSpan.FromMinutes(1);
        await Stores.Baseline(Neelam).SaveAsync(new TemplateBaseline([BlockType.Offer]));

        await Stores.UseStandardBaselineAsync(Neelam, Actor.Demo);

        var inForce = await Stores.BaselineInForceAsync(Neelam);
        Assert.False(inForce.IsOwn);
        Assert.Equal(TemplateBaseline.Standard.Parts, inForce.Baseline.Parts);
        Assert.Empty(_containers.For(Neelam.Value).Blobs);
    }

    // Saving it empty is how a client opts out: its own, and nothing in it.
    [Fact]
    public async Task An_empty_own_baseline_means_no_warnings()
    {
        await Stores.Baseline(Neelam).SaveAsync(TemplateBaseline.None);

        var inForce = await Stores.BaselineInForceAsync(Neelam);

        Assert.True(inForce.IsOwn);
        Assert.Empty(inForce.Baseline.MissingFrom(Template(new TemplateBlock("Note", BlockType.Paragraphs))));
    }

    // A short note such as "we're closed Monday" may rightly have no button: it saves, and says so.
    [Fact]
    public async Task A_template_missing_a_part_saves_and_warns()
    {
        var session = TemplateSession.New(Stores.Campaigns(Neelam, Actor.Demo));
        session.Editor.Name = "Closed Monday";
        session.Editor.Add(BlockType.Header);
        session.Editor.Add(BlockType.Heading);
        session.Editor.Add(BlockType.Paragraphs);
        session.Editor.Add(BlockType.Image);
        session.Editor.Add(BlockType.SignOff);

        var saved = await session.SaveAsync();

        Assert.Equal(saved, session.Latest);
        var gap = Assert.Single(session.Editor.Missing((await Stores.BaselineInForceAsync(Neelam)).Baseline));
        Assert.Equal(BlockType.Button, gap.Part);
    }

    // Her Membership template's photo is optional, and it still counts.
    [Fact]
    public void An_optional_block_counts_as_present()
    {
        var template = Template(
            new TemplateBlock("Header", BlockType.Header),
            new TemplateBlock("Headline", BlockType.Heading),
            new TemplateBlock("Photo", BlockType.Image, Required: false),
            new TemplateBlock("Join", BlockType.Button, Required: false),
            new TemplateBlock("Sign-off", BlockType.SignOff));

        Assert.Empty(TemplateBaseline.Standard.MissingFrom(template));
        // The editor, where the warnings show, counts it too.
        Assert.Empty(TemplateEditor.Open(template).Missing(TemplateBaseline.Standard));
    }

    [Fact]
    public void The_warning_names_the_part()
    {
        var gap = Assert.Single(new TemplateBaseline([BlockType.SignOff, BlockType.Heading])
            .MissingFrom(Template(new TemplateBlock("Headline", BlockType.Heading))));

        Assert.Equal("Your templates usually have a sign-off; this one doesn't.", gap.Message);
    }

    [Fact]
    public void Every_block_type_can_be_named_in_a_warning()
    {
        foreach (var type in Enum.GetValues<BlockType>())
            Assert.Contains(BaselineGap.Naming(type), BaselineGap.For(type).Message);
    }

    // Opening another client's stores never reaches this client's container.
    [Fact]
    public async Task Another_client_s_baseline_is_never_read()
    {
        await Stores.Baseline(Neelam).SaveAsync(TemplateBaseline.None);
        var opened = new List<string>();
        var stores = new ClientStores(name => { opened.Add(name); return _containers.For(name); }, _clock, TimeSpan.FromDays(1), new InMemoryApprovals(), TestRecords.Unwatched);

        var inForce = await stores.BaselineInForceAsync(Other);

        Assert.False(inForce.IsOwn);
        Assert.Equal(TemplateBaseline.Standard.Parts, inForce.Baseline.Parts);
        Assert.DoesNotContain(Neelam.Value, opened);
        Assert.Contains(Other.Value, opened);
    }

    [Fact]
    public void A_baseline_round_trips_as_json_empty_included()
    {
        Assert.Equal(TemplateBaseline.Standard.Parts,
            CampaignJson.DeserializeBaseline(CampaignJson.SerializeBaseline(TemplateBaseline.Standard)).Parts);
        Assert.Empty(CampaignJson.DeserializeBaseline(CampaignJson.SerializeBaseline(TemplateBaseline.None)).Parts);
        Assert.Throws<InvalidDataException>(() => CampaignJson.DeserializeBaseline("""{"schema":2}"""));
    }

    [Fact]
    public void A_part_is_listed_once_and_must_be_a_block_type()
    {
        Assert.Equal([BlockType.Button], new TemplateBaseline([BlockType.Button, BlockType.Button]).Parts);
        Assert.Throws<ArgumentException>(() => new TemplateBaseline([(BlockType)99]));
    }
}

/// <summary>The Templates page's baseline section, through the enforcing app.</summary>
public class TemplatesPageBaselineTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord SalonOne = new(
        new ClientName("test-salon-one"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "Salon One");

    private async Task<string> TemplatesPage()
    {
        if ((await app.Clients.ListAsync()).Count == 0) await app.Clients.AddAsync(SalonOne);
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        client.SignedIn([Features.Templates], [SalonOne.GroupId]);
        var response = await client.GetAsync("/templates");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    [Fact]
    public async Task It_shows_the_baseline_in_force_and_where_it_comes_from()
    {
        await app.Stores.UseStandardBaselineAsync(SalonOne.Name, Actor.Demo);
        var standard = await TemplatesPage();

        Assert.Contains("Every template should have", standard);
        Assert.Contains("The standard baseline.", standard);
        Assert.Contains("<li>Sign-off</li>", standard);
        Assert.Contains("Save it empty", standard);
        Assert.DoesNotContain("Use the standard baseline", standard);

        await app.Stores.Baseline(SalonOne.Name).SaveAsync(new TemplateBaseline([BlockType.Greeting]));
        var own = await TemplatesPage();
        await app.Stores.UseStandardBaselineAsync(SalonOne.Name, Actor.Demo);

        Assert.Contains("Your own baseline.", own);
        Assert.Contains("<li>Greeting</li>", own);
        Assert.DoesNotContain("<li>Sign-off</li>", own);
        Assert.Contains("Use the standard baseline", own);
    }
}
