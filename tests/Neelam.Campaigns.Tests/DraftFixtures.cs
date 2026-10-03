using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

/// <summary>Drafts built through the template and tier-copy path, shared by draft and storage tests.</summary>
internal static class DraftFixtures
{
    private static readonly Campaign Target = SampleCampaigns.Corrected();

    /// <summary>
    /// Neelam's membership-announcement template, as data: the corrected email's blocks, with the
    /// parts that stay the same from send to send fixed and everything else left to fill.
    /// </summary>
    public static readonly CampaignTemplate Membership = new("Membership announcement",
    [
        new TemplateBlock("Headline", BlockType.Heading),
        new TemplateBlock("Greeting", BlockType.Greeting, Fixed: Target.Block<GreetingBlock>("Greeting")),
        new TemplateBlock("Opening", BlockType.Paragraphs),
        new TemplateBlock("Offer", BlockType.Offer, Recurring: true),
        new TemplateBlock("Call to action", BlockType.Button),
        new TemplateBlock("Closing", BlockType.Paragraphs, Fixed: Target.Block<ParagraphsBlock>("Closing")),
        new TemplateBlock("Sign-off", BlockType.SignOff, Fixed: Target.Block<SignOffBlock>("Sign-off")),
        new TemplateBlock("Disclaimer", BlockType.FinePrint, Required: false, Fixed: Target.Block<FinePrintBlock>("Disclaimer")),
    ]);

    /// <summary>Everything a campaign author types, apart from the tiers.</summary>
    public static CampaignDraft StartAndFillText(CampaignTemplate? template = null)
    {
        var d = (template ?? Membership).Start();
        d.Subject.Set(Target.Subject);
        d.Text("Headline").Set(Target.Block<HeadingBlock>("Headline").Text);
        d.Paragraphs("Opening").Set(Target.Block<ParagraphsBlock>("Opening").Paragraphs);
        d.Button("Call to action").Set(Target.Block<ButtonBlock>("Call to action").Action);
        var o = d.Offer("Offer");
        var offer = Target.OfferOf();
        o.Name.Set(offer.Name);
        o.Summary.Set(offer.Summary);
        o.TiersNote.Set(offer.TiersNote!);
        o.TermsUrl.Set(offer.TermsUrl!);
        return d;
    }

    public static TierDraft AddGold(OfferDraft o)
    {
        var gold = o.AddTier();
        gold.Name.Set("Gold Member");
        gold.MonthlyPrice.Set(149m);
        gold.AddBenefit(new BirthdayCredit(25m));
        gold.AddBenefit(new PercentOff(5, "any qualifying treatments"));
        gold.AddBenefit(new DiscountedItem(50, "wellness injection", "per visit"));
        return gold;
    }

    /// <summary>Second send, replayed: tier 2 copied, two benefits updated, name and benefit 3 untouched.</summary>
    public static CampaignDraft SecondSendReplayed()
    {
        var d = StartAndFillText();
        var o = d.Offer("Offer");
        AddGold(o);
        var platinum = o.CopyTier(0);
        platinum.MonthlyPrice.Set(299m);
        platinum.Benefits[0].Set(new BirthdayCredit(75m));
        platinum.Benefits[1].Set(new PercentOff(10, "any qualifying treatments"));
        return d;
    }

    /// <summary>The corrected email, written properly through the draft.</summary>
    public static CampaignDraft Finished(CampaignTemplate? template = null)
    {
        var d = StartAndFillText(template);
        var o = d.Offer("Offer");
        AddGold(o);
        var platinum = o.CopyTier(0);
        platinum.Name.Set("Platinum Member");
        platinum.MonthlyPrice.Set(299m);
        platinum.Benefits[0].Set(new BirthdayCredit(75m));
        platinum.Benefits[1].Set(new PercentOff(10, "any qualifying treatments"));
        platinum.Benefits[2].Set(new FreeItem(1, "wellness injection", "per visit"));
        platinum.AddBenefit(new DiscountedItem(50, "wellness injection", "per visit", "any additional"));
        return d;
    }
}
