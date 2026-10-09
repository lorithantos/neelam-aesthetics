using System.Net;
using Neelam.Campaigns.Storage;
using Neelam.Web.Components.Pages;
using Neelam.Web.Security;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The phone numbers a client may publish are its registration, in the clients table, and the
/// checks flag any other number in a campaign. Owner's decision, 2026-10-09, after an email signed
/// off with 425-877-8646 while Square had the business at (425) 773-5261.
/// </summary>
public class RegisteredPhoneTests
{
    private const string Rule = "phone-registered";

    private static PhoneNumber Phone(string text) =>
        PhoneNumber.TryParse(text, out var number) ? number : throw new ArgumentException(text);

    private static BusinessContext Registered(params string[] numbers) =>
        new("Neelam Aesthetics") { Phones = new PhoneNumbers(numbers.Select(Phone)) };

    private static IReadOnlyList<Finding> PhoneFindings(Campaign campaign, BusinessContext? business) =>
        CampaignReview.Check(campaign, business: business).Findings.Where(f => f.Rule == Rule).ToList();

    // Each way a number has been seen written, in the block it was seen in: the latest email's
    // sign-off, Square's record of the business, and a tel: link.
    public static TheoryData<string, Campaign, string, string> Patterns => new()
    {
        {
            "Sign-off › Tagline",
            new Campaign("Hello", [new SignOffBlock("Sign-off", new SignOff("With gratitude,", "Neelam Aesthetics Team", "Snohomish, WA | 425-877-8646"))]),
            "425-877-8646",
            "4258778646"
        },
        {
            "Closing, paragraph 2",
            new Campaign("Hello", [new ParagraphsBlock("Closing", ["See you soon.", "Book at (425) 773-5261 today."])]),
            "(425) 773-5261",
            "4257735261"
        },
        {
            "Call us › Link",
            new Campaign("Hello", [new ButtonBlock("Call us", new CallToAction("Call to book", new Uri("tel:+14257735261")))]),
            "+14257735261",
            "4257735261"
        },
    };

    [Theory]
    [MemberData(nameof(Patterns))]
    public void A_registered_number_draws_nothing(string location, Campaign campaign, string written, string number)
    {
        _ = (location, written);
        Assert.Empty(PhoneFindings(campaign, Registered("(425) 000-0000", number)));
    }

    [Theory]
    [MemberData(nameof(Patterns))]
    public void An_unregistered_number_is_worth_a_look_naming_it_and_where_it_is(
        string location, Campaign campaign, string written, string number)
    {
        _ = number;
        var finding = Assert.Single(PhoneFindings(campaign, Registered("(425) 000-0000")));

        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Equal(location, finding.Location);
        Assert.Equal(written, finding.Excerpt);
        // One number registered: named once, with no brackets around its own ("((425) 000-0000)").
        Assert.Equal($"{written} isn't your registered number, (425) 000-0000. Check it before sending.", finding.Message);
        Assert.DoesNotContain("((", finding.Message);
    }

    // Before the client has registered a number there is nothing to hold the email to.
    [Theory]
    [MemberData(nameof(Patterns))]
    public void With_no_numbers_registered_nothing_is_said(string location, Campaign campaign, string written, string number)
    {
        _ = (location, written, number);
        Assert.Empty(PhoneFindings(campaign, null));
        Assert.Empty(PhoneFindings(campaign, new BusinessContext("Neelam Aesthetics")));
    }

    [Fact]
    public void Two_registered_numbers_are_both_allowed_and_a_third_is_not()
    {
        var campaign = new Campaign("Hello",
        [
            new ParagraphsBlock("Opening", ["Call (425) 773-5261, or book on 425-877-8646.", "Or try 206-555-0100."]),
        ]);

        var finding = Assert.Single(PhoneFindings(campaign, Registered("425-877-8646", "(425) 773-5261")));
        Assert.Equal("Opening, paragraph 2", finding.Location);
        Assert.Equal("206-555-0100 isn't one of your registered numbers: (425) 877-8646, (425) 773-5261. Check it before sending.", finding.Message);
    }

    [Theory]
    [InlineData("425.877.8646")]
    [InlineData("(425) 877-8646")]
    [InlineData("425-877-8646")]
    [InlineData("425 877 8646")]
    [InlineData("4258778646")]
    [InlineData("+1 425 877 8646")]
    [InlineData("1-425-877-8646")]
    [InlineData("tel:+14258778646")]
    public void Every_way_of_writing_a_number_is_the_same_number(string written)
    {
        Assert.True(PhoneNumber.TryParse(written, out var number));
        Assert.Equal(Phone("(425) 877-8646"), number);
        Assert.Equal("14258778646", number.Digits);
        Assert.Equal("(425) 877-8646", number.Formatted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("877-8646")]
    [InlineData("call the front desk")]
    [InlineData("425-877-86460")]
    public void What_is_not_a_number_with_its_area_code_is_refused(string written) =>
        Assert.False(PhoneNumber.TryParse(written, out _));

    // Registered one way, written another: still the registered number.
    [Fact]
    public void A_number_registered_with_dots_matches_it_written_with_brackets()
    {
        var campaign = new Campaign("Hello", [new ParagraphsBlock("Opening", ["Call (425) 877-8646."])]);

        Assert.Empty(PhoneFindings(campaign, Registered("425.877.8646")));
        Assert.Single(PhoneFindings(campaign, Registered("425.877.8647")));
    }

    // The sign-off is fixed by the template, so the number in it reaches every campaign: it is
    // checked from the first keystroke, while the rest of the campaign is still missing.
    [Fact]
    public void A_number_in_the_template_s_fixed_text_is_checked_while_parts_are_missing()
    {
        var editor = CampaignEditor.Start(SignedOffWith("Snohomish, WA | 425-877-8646"));

        var status = editor.Status(business: Registered("(425) 773-5261"));

        Assert.NotEmpty(status.Missing);
        var finding = Assert.Single(status.Findings, f => f.Rule == Rule);
        Assert.Equal("Sign-off › Tagline", finding.Location);
        Assert.Empty(editor.Status(business: Registered("425-877-8646")).Findings.Where(f => f.Rule == Rule));
    }

    [Fact]
    public async Task The_gate_checks_the_numbers_against_the_business_it_is_given()
    {
        var campaign = new Campaign("Hello", [new ParagraphsBlock("Opening", ["Call 425-877-8646."])]);

        var report = await CampaignGate.ReviewAsync(campaign, new FakeProofreader(), business: Registered("(425) 773-5261"));

        Assert.Contains(report.Findings, f => f.Rule == Rule && f.Excerpt == "425-877-8646");
    }

    // ---- The registration

    private static readonly ClientRecord Neelam = new(
        new ClientName("neelam-aesthetics"), Guid.Parse("33333333-3333-3333-3333-333333333333"), "Neelam Aesthetics");

    [Fact]
    public void The_admin_form_round_trips_the_numbers()
    {
        var (client, errors) = new ClientForm
        {
            Name = "neelam-aesthetics",
            GroupId = Neelam.GroupId.ToString(),
            DisplayName = "Neelam Aesthetics",
            Phones = "425.877.8646\r\n\r\n (425) 773-5261 ",
        }.ToRecord();

        Assert.Empty(errors);
        Assert.Equal(["14258778646", "14257735261"], client!.Phones.Select(p => p.Digits));
        Assert.Equal(client.Phones, client.Business.Phones);

        var form = ClientForm.Of(client);
        Assert.Equal("(425) 877-8646\n(425) 773-5261", form.Phones);
        Assert.Equal(client, form.ToRecord().Client);
    }

    [Fact]
    public void The_admin_form_says_which_number_it_cannot_read()
    {
        var (client, errors) = new ClientForm
        {
            Name = "neelam-aesthetics", GroupId = Neelam.GroupId.ToString(), DisplayName = "Neelam Aesthetics",
            Phones = "425-877-8646\n877-8646",
        }.ToRecord();

        Assert.Null(client);
        Assert.StartsWith("\"877-8646\" is not a phone number", Assert.Single(errors));
    }

    [Fact]
    public void The_clients_table_keeps_the_numbers_as_digits()
    {
        var registered = Neelam with { Phones = new PhoneNumbers([Phone("425-877-8646"), Phone("(425) 773-5261")]) };

        var row = TableMetadata.FromClient(registered);

        Assert.Equal("14258778646,14257735261", row.GetString("Phones"));
        Assert.Equal(registered, TableMetadata.ToClient(row));
    }

    // Rows written before numbers were registered have no Phones property at all.
    [Fact]
    public void An_old_row_with_no_numbers_still_reads()
    {
        var row = TableMetadata.FromClient(Neelam);
        Assert.False(row.ContainsKey("Phones"));

        var client = TableMetadata.ToClient(row);

        Assert.Empty(client.Phones);
        Assert.Equal(Neelam, client);
    }

    internal static CampaignTemplate SignedOffWith(string tagline) => new("Announcement",
    [
        new TemplateBlock("Headline", BlockType.Heading),
        new TemplateBlock("Sign-off", BlockType.SignOff,
            Fixed: new SignOffBlock("Sign-off", new SignOff("With gratitude,", "Neelam Aesthetics Team", tagline))),
    ]);
}

/// <summary>The campaign page holds a campaign to its client's registered numbers.</summary>
public class RegisteredPhonePageTests(EnforcedApp app) : IClassFixture<EnforcedApp>
{
    private static readonly ClientRecord SalonOne = new ClientRecord(
        new ClientName("test-salon-one"), Guid.Parse("11111111-1111-1111-1111-111111111111"), "Salon One")
    {
        Phones = new PhoneNumbers([PhoneNumber.TryParse("(425) 773-5261", out var n) ? n : throw new InvalidOperationException()]),
    };

    private static readonly Guid Template = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000f1");

    [Fact]
    public async Task A_new_campaign_flags_the_template_s_unregistered_number()
    {
        if ((await app.Clients.ListAsync()).Count == 0)
        {
            await app.Clients.AddAsync(SalonOne);
            await app.Stores.Campaigns(SalonOne.Name, Actor.Demo)
                .SaveTemplateAsync(Template, RegisteredPhoneTests.SignedOffWith("Snohomish, WA | 425-877-8646"));
        }
        var client = app.CreateClient(new() { AllowAutoRedirect = false });
        client.SignedIn([Features.Campaigns], [SalonOne.GroupId]);

        var response = await client.GetAsync($"/campaigns/new/{Template}");
        var page = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("425-877-8646 isn't your registered number, (425) 773-5261. Check it before sending.", page);
    }
}
