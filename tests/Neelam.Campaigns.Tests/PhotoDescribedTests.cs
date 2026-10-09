using System.Text.Json.Nodes;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Every photo needs a description (owner, 2026-10-09: "Rule now, we will allow the LLM to read the
/// image when it is hooked up"). Neelam's back-to-school email (Aug 2026) carried its whole offer
/// inside one image with empty alt text, where no rule could read it and readers with images off got
/// a heading and a button. The description is the block's own, the alt text the export hands Square.
/// </summary>
public class PhotoDescribedTests
{
    private const string Rule = "photo-described";

    // Approved, and shown what is worth a look at export and gone on past (ExportWarningsTests): a
    // warning is listed once before the export, and never stops it.
    private static readonly Approval ByPriya = new("Priya", new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero))
    {
        WarningsSeen = new("Priya", new DateTimeOffset(2026, 10, 9, 12, 1, 0, TimeSpan.Zero)),
    };

    private static string Message(string name) =>
        $"The photo '{name}' has no description. Describe what it shows, and write any offer or dates it contains " +
        "in the email text too: words in a picture can't be checked, and some readers never see it.";

    private static IReadOnlyList<Finding> PhotoFindings(Campaign campaign, BusinessContext? business = null) =>
        CampaignReview.Check(campaign, business: business).Findings.Where(f => f.Rule == Rule).ToList();

    private static Campaign Header(string? description) =>
        new("Back to school", [new HeaderBlock("Header", "Neelam Aesthetics", new ImageRef("Back to school offer", description))]);

    private static Campaign Image(string? description) =>
        new("Back to school", [new ImageBlock("Offer photo", new ImageRef("Back to school offer", description))]);

    public static TheoryData<Campaign, string> Undescribed => new()
    {
        { Header(null), "Header › Photo" },
        { Header("   "), "Header › Photo" },
        { Image(null), "Offer photo" },
        { Image(""), "Offer photo" },
    };

    [Theory]
    [MemberData(nameof(Undescribed))]
    public void A_photo_with_no_description_is_worth_a_look_where_it_is(Campaign campaign, string location)
    {
        var finding = Assert.Single(PhotoFindings(campaign));

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(location, finding.Location);
        Assert.Equal(Message("Back to school offer"), finding.Message);
    }

    [Fact]
    public void A_description_silences_it()
    {
        const string described = "Teachers' offer: $50 off 30+ units, September 1-30";
        Assert.Empty(PhotoFindings(Header(described)));
        Assert.Empty(PhotoFindings(Image(described)));
    }

    [Fact]
    public void A_header_with_no_photo_has_nothing_to_describe() =>
        Assert.Empty(PhotoFindings(new Campaign("Hello", [new HeaderBlock("Header", "Neelam Aesthetics")])));

    // Worth a look, never a block: an approved campaign with an undescribed photo still exports.
    [Fact]
    public void It_never_stops_the_export()
    {
        var campaign = Undescribe(BeautyBankEmail.Corrected());
        var report = CampaignGate.DemoReview(campaign, ByPriya.Seeing(campaign));

        Assert.Contains(report.Findings, f => f.Rule == Rule);
        Assert.True(report.CanExport);
    }

    // Both real sends had empty alt text on both photos; the corrected version describes them.
    [Fact]
    public void The_Beauty_Bank_sends_are_flagged_and_the_corrected_email_is_not()
    {
        foreach (var send in new[] { BeautyBankEmail.FirstSend(), BeautyBankEmail.SecondSend() })
        {
            var findings = PhotoFindings(send);
            Assert.Equal(["Header › Photo", "Photo"], findings.Select(f => f.Location));
            Assert.Equal([Message("Principals toasting"), Message("Principals seated")], findings.Select(f => f.Message));
        }
        Assert.Empty(PhotoFindings(BeautyBankEmail.Corrected()));
    }

    // A template's fixed photo goes out in every campaign from it, so it is checked in the campaign
    // and said in the template's advice.
    [Fact]
    public void A_fixed_template_photo_is_covered()
    {
        var template = WithUndescribedHeader(DraftFixtures.Membership);

        var campaign = DraftFixtures.Finished(template).Build().Campaign!;
        var finding = Assert.Single(PhotoFindings(campaign));
        Assert.Equal("Header › Photo", finding.Location);

        var advice = Assert.Single(TemplateAdvice.For(template.Blocks), f => f.Rule == Rule);
        Assert.Equal(Severity.Warning, advice.Severity);
        Assert.EndsWith(Message("Principals toasting"), advice.Message);

        // Her membership template describes its header photo.
        Assert.DoesNotContain(TemplateAdvice.For(DraftFixtures.Membership.Blocks), f => f.Rule == Rule);
        Assert.Empty(PhotoFindings(DraftFixtures.Finished().Build().Campaign!));
    }

    // A photo the library does not hold already says so beside its field and in the preview; the
    // rule adds nothing to that. One the library holds is matched as the library finds it.
    [Fact]
    public void A_photo_not_in_the_library_gets_only_the_library_s_note()
    {
        var campaign = Undescribe(BeautyBankEmail.Corrected());
        var library = new BusinessContext("Neelam Aesthetics") { LibraryPhotos = ["  principals SEATED "] };

        var finding = Assert.Single(PhotoFindings(campaign, library));
        Assert.Equal("Photo", finding.Location);
        Assert.Empty(PhotoFindings(campaign, new BusinessContext("Neelam Aesthetics") { LibraryPhotos = [] }));
        // A business whose library was not read checks every photo.
        Assert.Equal(2, PhotoFindings(campaign, new BusinessContext("Neelam Aesthetics")).Count);

        var template = WithUndescribedHeader(DraftFixtures.Membership);
        Assert.DoesNotContain(TemplateAdvice.For(template.Blocks, libraryPhotos: ["Principals seated"]), f => f.Rule == Rule);
        Assert.Contains(TemplateAdvice.For(template.Blocks, libraryPhotos: ["Principals toasting"]), f => f.Rule == Rule);
    }

    // The assistant reads the same findings as the page, in "Worth a look".
    [Fact]
    public void It_reaches_the_assistant_export_s_worth_a_look()
    {
        var campaign = BeautyBankEmail.Corrected();
        campaign = campaign with
        {
            Blocks = campaign.Blocks.Select(b => b is ImageBlock i ? i with { Image = i.Image with { AltText = null } } : b).ToList(),
        };
        var report = CampaignGate.DemoReview(campaign, ByPriya.Seeing(campaign));

        var json = ExportSchema.Valid(AssistantExport.Json(report));
        var worthALook = JsonNode.Parse(json)!["review"]!["worthALook"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

        Assert.Contains($"Photo: {Message("Principals seated")}", worthALook);
        Assert.DoesNotContain(worthALook, w => w.Contains("Principals toasting"));
    }

    // The corrected email with both photos' descriptions taken away, as the sends had them.
    private static Campaign Undescribe(Campaign campaign) => campaign with
    {
        Blocks = campaign.Blocks.Select(b => b switch
        {
            HeaderBlock { Photo: { } p } h => h with { Photo = p with { AltText = null } },
            ImageBlock i => i with { Image = i.Image with { AltText = null } },
            _ => b,
        }).ToList(),
    };

    private static CampaignTemplate WithUndescribedHeader(CampaignTemplate template) => new(template.Name,
        template.Blocks.Select(b => b.Fixed is HeaderBlock { Photo: { } p } h
            ? b with { Fixed = h with { Photo = p with { AltText = null } } }
            : b).ToList());
}
