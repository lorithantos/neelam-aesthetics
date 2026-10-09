using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

/// <summary>Drafts built through the template and tier-copy path, shared by draft and storage tests.</summary>
internal static class DraftFixtures
{
    private static readonly Campaign Target = SampleCampaigns.Corrected();

    /// <summary>
    /// Neelam's membership-announcement template. The content lives in
    /// <see cref="BeautyBankEmail.MembershipTemplate"/>, since the How it works page shows it too;
    /// every test that builds a draft from it reads the library's copy.
    /// </summary>
    public static readonly CampaignTemplate Membership = BeautyBankEmail.MembershipTemplate();

    /// <summary>Everything a campaign author types, apart from the tiers.</summary>
    public static CampaignDraft StartAndFillText(CampaignTemplate? template = null)
    {
        var d = (template ?? Membership).Start();
        d.Subject.Set(Target.Subject);
        d.Text("Headline").Set(Target.Block<HeadingBlock>("Headline").Text);
        d.Paragraphs("Opening").Set(Target.Block<ParagraphsBlock>("Opening").Paragraphs);
        d.Button("Call to action").Set(Target.Block<ButtonBlock>("Call to action").Action);
        d.Image("Photo").Set(Target.Block<ImageBlock>("Photo").Image);
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

    /// <summary>
    /// Second send, replayed: tier 1 as it went out, "Platinum Member" at $149; tier 2 copied, its
    /// price, two benefits and a fourth as they went out, its name and benefit 3 untouched. The copy
    /// keeps the name, so both tiers are "Platinum Member", as both sent tiers were
    /// (<see cref="BeautyBankEmail.SecondSend"/>). Benefit 3 is the one step left undone: still the
    /// copy of tier 1's, where the send had a free injection, so the draft cannot build.
    /// </summary>
    public static CampaignDraft SecondSendReplayed()
    {
        var d = StartAndFillText();
        var o = d.Offer("Offer");
        AddGold(o).Name.Set("Platinum Member");
        var copy = o.CopyTier(0);
        copy.MonthlyPrice.Set(299m);
        copy.Benefits[0].Set(new BirthdayCredit(75m));
        copy.Benefits[1].Set(new PercentOff(10, "any qualifying treatments"));
        copy.AddBenefit(new DiscountedItem(50, "wellness injection", "per visit", "any additional"));
        return d;
    }

    /// <summary>
    /// Gold copied, every copied benefit changed or confirmed, and the name and price left as they
    /// were copied: it builds, and the rules stop it until either tier changes each.
    /// </summary>
    public static CampaignDraft CopiedAsItStands()
    {
        var d = StartAndFillText();
        var o = d.Offer("Offer");
        AddGold(o);
        var copy = o.CopyTier(0);
        copy.Benefits[0].Set(new BirthdayCredit(75m));
        copy.Benefits[1].Set(new PercentOff(10, "any qualifying treatments"));
        copy.Benefits[2].Confirm();
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

    /// <summary>
    /// Finished, but with both tiers named "Platinum Member", as the second send had them, and no
    /// terms link, as the email went out: one part missing, and a mistake the rules catch.
    /// </summary>
    public static CampaignDraft SameNamesNoTerms()
    {
        var d = Finished();
        var o = d.Offer("Offer");
        o.Tiers[0].Name.Set("Platinum Member");
        o.TermsUrl.Clear();
        return d;
    }
}
