namespace Neelam.Campaigns.Tests;

/// <summary>
/// The sample must not misstate what was sent. The first send, checked word for word against the
/// email as it went out (2026-10-09), as close as typed benefits come to it.
/// </summary>
public class BeautyBankEmailTests
{
    private static readonly Campaign First = BeautyBankEmail.FirstSend();

    private static T Block<T>(string label) where T : Block => First.Blocks.OfType<T>().Single(b => b.Label == label);

    [Fact]
    public void The_first_send_s_options_are_named_and_worded_as_sent()
    {
        var offer = Block<OfferBlock>("Offer").Offer;

        Assert.Equal("Introducing: ✨The Neelam Aesthetics Beauty Bank✨", offer.Name);
        Assert.Equal("Here’s how it works, 100% of your money goes to any treatments you would like:", offer.TiersNote);
        Assert.Equal(["Option 1 Platinum Member", "Option 2 Platinum Member"], offer.Tiers.Select(t => t.Name));
        foreach (var tier in offer.Tiers)
        {
            Assert.Equal(299m, tier.MonthlyPrice);
            Assert.Equal(
                [
                    "$75 birthday credit during your birth month",
                    "10% off any qualifying treatments",
                    "1 complimentary Wellness Injection per visit",
                    "50% off any additional wellness injections during visit",
                ],
                tier.Benefits.Select(b => b.Describe()));
        }
    }

    [Fact]
    public void The_first_send_s_greeting_and_opening_are_as_sent()
    {
        Assert.Equal("Hi Beautiful🤍", Block<GreetingBlock>("Greeting").Text);
        var opening = Block<ParagraphsBlock>("Opening").Paragraphs;
        Assert.Equal(3, opening.Count);
        Assert.Contains("Your support for our small Family business has meant more to us than we can put into words.", opening[1]);
        // The second send is left as it was: only the first was checked against its email.
        Assert.Contains("small family business",
            BeautyBankEmail.SecondSend().Blocks.OfType<ParagraphsBlock>().Single(b => b.Label == "Opening").Paragraphs[1]);
    }
}
