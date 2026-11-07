using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Neelam.Campaigns.Storage;
using Neelam.Web.Components.Shared;
using KnownItemsPage = Neelam.Web.Components.Pages.KnownItemsPage;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The fields a page holds (the offer, promotion, benefit and photo fields), each alone as a circuit holds
/// it: what is typed in them tells the page around them, which Blazor does not do by itself; and the
/// known-item pickers in them and on the Known items page. Written on 2026-11-03, step 2 of the DRY plan,
/// against the copies before each was made one.
/// </summary>
public class FieldsTellThePageTests(DemoApp app) : IClassFixture<DemoApp>
{
    private static readonly KnownBenefit TenOff = new(KnownItem.NewId(), new PercentOff(10, "any qualifying treatments"));
    private static readonly KnownTier Platinum = new(KnownItem.NewId(), "Platinum Member", 299m, [new BirthdayCredit(75m)]);
    private static readonly KnownItems Known = new([TenOff, Platinum]);

    private int _changed;

    private EventCallback Changed => new(null, (Action)(() => _changed++));

    private static CampaignEditor Start(BlockType type) =>
        CampaignEditor.Start(new CampaignTemplate("Fields", [new(type.ToString(), type)]));

    private static OfferEditor Offer() => Start(BlockType.Offer).Blocks.OfType<OfferBlockEditor>().Single().Offer;

    private static PromotionEditor Promotion() => Start(BlockType.Promotion).Blocks.OfType<PromotionBlockEditor>().Single().Promotion;

    private Task<OpenCampaignPage> OfferFieldsAsync(OfferEditor offer) =>
        OpenCampaignPage.OpenAsync<OfferFields>(app, new Dictionary<string, object?>
        {
            [nameof(OfferFields.Offer)] = offer,
            [nameof(OfferFields.Index)] = 0,
            [nameof(OfferFields.Known)] = Known,
            [nameof(OfferFields.Changed)] = Changed,
        });

    private Task<OpenCampaignPage> PromotionFieldsAsync(PromotionEditor promotion) =>
        OpenCampaignPage.OpenAsync<PromotionFields>(app, new Dictionary<string, object?>
        {
            [nameof(PromotionFields.Promotion)] = promotion,
            [nameof(PromotionFields.Index)] = 0,
            [nameof(PromotionFields.Known)] = Known,
            [nameof(PromotionFields.Changed)] = Changed,
        });

    // What the field with this id holds as the page shows it.
    private static string ValueOf(string html, string id)
    {
        var input = Regex.Match(html, $"<input id=\"{Regex.Escape(id)}\"[^>]*>");
        Assert.True(input.Success, $"one field with the id {id}");
        return Regex.Match(input.Value, " value=\"([^\"]*)\"").Groups[1].Value;
    }

    // ---- The fields tell the page

    [Fact]
    public async Task Typing_in_an_offer_tells_the_page()
    {
        var offer = Offer();
        await using var page = await OfferFieldsAsync(offer);

        await page.TypeAsync("offer-name-0", "Beauty Bank");

        Assert.Equal("Beauty Bank", offer.Name.Text);
        Assert.Equal(1, _changed);
    }

    // A benefit's own fields tell the offer, which draws itself again (Copy tier waits for every
    // benefit to be filled in) and tells the page.
    [Fact]
    public async Task Typing_in_a_tier_s_benefit_redraws_the_offer_and_tells_the_page()
    {
        var offer = Offer();
        offer.AddTier().AddBenefit().Kind = "birthday-credit";
        await using var page = await OfferFieldsAsync(offer);
        Assert.Contains("disabled title=\"Fill in or remove its empty benefits first\">Copy tier</button>", await page.HtmlAsync());

        await page.TypeIntoAsync("benefit-amount-0-t0-b0", "75");

        Assert.Contains("<button class=\"button\">Copy tier</button>", await page.HtmlAsync());
        Assert.Equal(1, _changed);
    }

    [Fact]
    public async Task Typing_in_a_promotion_tells_the_page()
    {
        var promotion = Promotion();
        await using var page = await PromotionFieldsAsync(promotion);

        await page.TypeIntoAsync("promotion-name-0", "Filler Promo");

        Assert.Equal("Filler Promo", promotion.Name.Text);
        Assert.Equal(1, _changed);
    }

    [Fact]
    public async Task Typing_in_a_benefit_tells_the_page_and_says_how_it_reads()
    {
        var benefit = Offer().AddTier().AddBenefit();
        benefit.Kind = "birthday-credit";
        await using var page = await OpenCampaignPage.OpenAsync<BenefitFields>(app, new Dictionary<string, object?>
        {
            [nameof(BenefitFields.Benefit)] = benefit,
            [nameof(BenefitFields.Id)] = "b",
            [nameof(BenefitFields.Changed)] = Changed,
        });

        await page.TypeIntoAsync("benefit-amount-b", "75");

        Assert.Contains("<p class=\"field-help\">Reads: $75 birthday credit during your birth month</p>", await page.HtmlAsync());
        Assert.Equal(1, _changed);
    }

    [Fact]
    public async Task Typing_a_photo_s_name_tells_the_page()
    {
        var photo = Start(BlockType.Image).Blocks.OfType<IPhotoChoice>().Single();
        await using var page = await OpenCampaignPage.OpenAsync<PhotoFields>(app, new Dictionary<string, object?>
        {
            [nameof(PhotoFields.Block)] = photo,
            [nameof(PhotoFields.Index)] = 0,
            [nameof(PhotoFields.Photos)] = Array.Empty<LibraryImage>(),
            [nameof(PhotoFields.Changed)] = Changed,
        });

        await page.TypeAsync("photo-0", "Hero");

        Assert.Equal("Hero", photo.PhotoName);
        Assert.Equal(1, _changed);
    }

    // ---- The known-item pickers: what she types stays in the box until it names one of hers; then
    // that one is added and the box is empty again.

    [Fact]
    public async Task Picking_a_known_benefit_adds_it_to_the_tier_and_empties_the_box()
    {
        var offer = Offer();
        var tier = offer.AddTier();
        await using var page = await OfferFieldsAsync(offer);

        await page.TypeIntoAsync("pick-benefit-0-t0", "10%");
        Assert.Equal("10%", ValueOf(await page.HtmlAsync(), "pick-benefit-0-t0"));
        Assert.Empty(tier.Benefits);

        await page.TypeIntoAsync("pick-benefit-0-t0", TenOff.Text);

        Assert.Equal(TenOff.Benefit, Assert.Single(tier.Benefits).Value);
        Assert.Equal("", ValueOf(await page.HtmlAsync(), "pick-benefit-0-t0"));
        Assert.Equal(2, _changed);
    }

    [Fact]
    public async Task Picking_a_known_tier_adds_it_to_the_offer_and_empties_the_box()
    {
        var offer = Offer();
        await using var page = await OfferFieldsAsync(offer);

        await page.TypeIntoAsync("known-tier-0", "Plat");
        Assert.Equal("Plat", ValueOf(await page.HtmlAsync(), "known-tier-0"));
        Assert.Empty(offer.Tiers);

        await page.TypeIntoAsync("known-tier-0", "platinum member");

        Assert.Equal("Platinum Member", Assert.Single(offer.Tiers).Name.Text);
        Assert.Equal("", ValueOf(await page.HtmlAsync(), "known-tier-0"));
        Assert.Equal(2, _changed);
    }

    [Fact]
    public async Task Picking_a_known_benefit_adds_a_promotion_line_and_empties_the_box()
    {
        var promotion = Promotion();
        await using var page = await PromotionFieldsAsync(promotion);

        await page.TypeIntoAsync("promotion-pick-benefit-0", "10%");
        Assert.Equal("10%", ValueOf(await page.HtmlAsync(), "promotion-pick-benefit-0"));
        Assert.Empty(promotion.Lines);

        await page.TypeIntoAsync("promotion-pick-benefit-0", TenOff.Text);

        Assert.Equal(TenOff.Benefit, Assert.IsType<BenefitLineEditor>(Assert.Single(promotion.Lines)).Benefit.Value);
        Assert.Equal("", ValueOf(await page.HtmlAsync(), "promotion-pick-benefit-0"));
        Assert.Equal(2, _changed);
    }

    [Fact]
    public async Task Picking_a_known_benefit_into_a_known_tier_adds_a_line_and_empties_the_box()
    {
        await app.KnownItems.AddAsync(DemoApp.Client, TenOff);
        await using var page = await OpenCampaignPage.OpenAtAsync<KnownItemsPage>(app, "known-items?show=tiers", new Dictionary<string, object?>());

        await page.ClickAsync("open-add-tier");
        await page.TypeIntoAsync("tier-pick-new", "10%");
        var typed = await page.HtmlAsync();
        Assert.Equal("10%", ValueOf(typed, "tier-pick-new"));
        Assert.DoesNotContain("id=\"benefit-kind-new-l0\"", typed);

        await page.TypeIntoAsync("tier-pick-new", TenOff.Text);

        var picked = await page.HtmlAsync();
        Assert.Contains("id=\"benefit-kind-new-l0\"", picked);
        Assert.Equal("", ValueOf(picked, "tier-pick-new"));
    }
}
