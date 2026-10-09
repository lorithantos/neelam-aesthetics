using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// The Beauty Bank email for the tests. The content lives in <see cref="BeautyBankEmail"/>, in the
/// library, since the How it works page shows it too; the tests keep this name so nothing that
/// reads it had to change, and the review tests that pin its exact findings pin the moved copy.
/// </summary>
internal static class SampleCampaigns
{
    public static Campaign FirstSend() => BeautyBankEmail.FirstSend();

    public static Campaign SecondSend() => BeautyBankEmail.SecondSend();

    public static Campaign Corrected() => BeautyBankEmail.Corrected();
}

/// <summary>Reading and replacing a campaign's blocks by label, for tests that change one part.</summary>
internal static class CampaignEdits
{
    public static T Block<T>(this Campaign c, string label) where T : Block =>
        c.Blocks.OfType<T>().Single(b => b.Label == label);

    public static Offer OfferOf(this Campaign c) => c.Block<OfferBlock>("Offer").Offer;

    /// <summary>The campaign with its offer's content replaced, keeping the offer's label and marker.</summary>
    public static Campaign WithOffer(this Campaign c, Offer offer) =>
        c.With(c.Block<OfferBlock>("Offer") with { Offer = offer });

    /// <summary>The campaign with the block of the same label replaced, or added at the end.</summary>
    public static Campaign With(this Campaign c, Block block)
    {
        var blocks = c.Blocks.ToList();
        var i = blocks.FindIndex(b => b.Label == block.Label);
        if (i >= 0) blocks[i] = block;
        else blocks.Add(block);
        return c with { Blocks = blocks };
    }

    public static Campaign Without(this Campaign c, string label) =>
        c with { Blocks = c.Blocks.Where(b => b.Label != label).ToList() };
}
