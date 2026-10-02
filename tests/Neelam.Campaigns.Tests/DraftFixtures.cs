using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

/// <summary>Drafts built through the template and tier-copy path, shared by draft and storage tests.</summary>
internal static class DraftFixtures
{
    private static readonly Campaign Target = SampleCampaigns.Corrected();

    /// <summary>A membership-announcement template made from the corrected email's fixed parts.</summary>
    public static readonly CampaignTemplate Membership = new(
        Name: "Membership announcement",
        Greeting: Target.Greeting,
        Closing: Target.Closing,
        SignOff: Target.SignOff,
        Disclaimer: Target.Disclaimer,
        HasOffer: true,
        IsRecurring: true);

    /// <summary>Everything a campaign author types, apart from the tiers.</summary>
    public static CampaignDraft StartAndFillText()
    {
        var d = Membership.Start();
        d.Subject.Set(Target.Subject);
        d.Headline.Set(Target.Headline);
        d.Opening.Set(Target.Opening);
        d.CallToAction.Set(Target.CallToAction!);
        var o = d.Offer!;
        o.Name.Set(Target.Offer!.Name);
        o.Summary.Set(Target.Offer.Summary);
        o.TiersNote.Set(Target.Offer.TiersNote!);
        o.TermsUrl.Set(Target.Offer.TermsUrl!);
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
        AddGold(d.Offer!);
        var platinum = d.Offer!.CopyTier(0);
        platinum.MonthlyPrice.Set(299m);
        platinum.Benefits[0].Set(new BirthdayCredit(75m));
        platinum.Benefits[1].Set(new PercentOff(10, "any qualifying treatments"));
        return d;
    }

    /// <summary>The corrected email, written properly through the draft.</summary>
    public static CampaignDraft Finished()
    {
        var d = StartAndFillText();
        AddGold(d.Offer!);
        var platinum = d.Offer!.CopyTier(0);
        platinum.Name.Set("Platinum Member");
        platinum.MonthlyPrice.Set(299m);
        platinum.Benefits[0].Set(new BirthdayCredit(75m));
        platinum.Benefits[1].Set(new PercentOff(10, "any qualifying treatments"));
        platinum.Benefits[2].Set(new FreeItem(1, "wellness injection", "per visit"));
        platinum.AddBenefit(new DiscountedItem(50, "wellness injection", "per visit", "any additional"));
        return d;
    }
}
