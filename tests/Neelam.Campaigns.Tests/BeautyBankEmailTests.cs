namespace Neelam.Campaigns.Tests;

/// <summary>
/// The sample must not misstate what was sent. Both sends, checked word for word against the
/// emails as they went out (2026-10-09), as close as typed benefits come to them.
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
    }

    // "WE'RE TURNING ONE!" was the headline of both sends; their inbox subjects were these.
    [Fact]
    public void Each_send_s_subject_is_its_inbox_subject_and_the_headline_is_the_headline()
    {
        Assert.Equal("Celebrate 1 year of Neelam Aesthetics!", First.Subject);
        Assert.Equal("Celebrate 1 year of Neelam Aesthetics! - Correction", BeautyBankEmail.SecondSend().Subject);
        Assert.Equal("WE’RE TURNING ONE! 🥂✨", BeautyBankEmail.SecondSend().Blocks.OfType<HeadingBlock>().Single().Text);
    }

    // The second send, checked word for word against the email as it went out (2026-10-09).
    [Fact]
    public void The_second_send_is_as_sent()
    {
        var second = BeautyBankEmail.SecondSend();
        var offer = second.Blocks.OfType<OfferBlock>().Single().Offer;

        Assert.Equal("Introducing: ✨The Neelam Aesthetics Beauty Bank✨", offer.Name);
        Assert.Equal(["Option 1 Platinum Member", "Option 2 Platinum Member"], offer.Tiers.Select(t => t.Name));
        Assert.Equal([149m, 299m], offer.Tiers.Select(t => t.MonthlyPrice));
        Assert.Equal(
            [
                "$25 birthday credit during your birth month",
                "5% off any qualifying treatments",
                // The email's real mistake, entered as sent: half off, or free?
                "50% off Complimentary Wellness Injections per visit",
            ],
            offer.Tiers[0].Benefits.Select(b => b.Describe()));
        Assert.Equal(
            [
                "$75 birthday credit during your birth month",
                "10% off any qualifying treatments",
                "1 complimentary Wellness Injection per visit",
                "50% off any additional wellness injections during visit",
            ],
            offer.Tiers[1].Benefits.Select(b => b.Describe()));
        Assert.Contains("small Family business", second.Blocks.OfType<ParagraphsBlock>().Single(b => b.Label == "Opening").Paragraphs[1]);
        Assert.Equal("Hi Beautiful🤍", second.Blocks.OfType<GreetingBlock>().Single().Text);
        Assert.Null(offer.TermsUrl);
        Assert.Empty(second.Blocks.OfType<FinePrintBlock>());
    }

    // The corrected version is not a send: it keeps its own subject and tiers.
    [Fact]
    public void The_corrected_version_is_unchanged()
    {
        var corrected = BeautyBankEmail.Corrected();
        var offer = corrected.Blocks.OfType<OfferBlock>().Single().Offer;

        Assert.Equal("WE’RE TURNING ONE!", corrected.Subject);
        Assert.Equal([("Gold Member", 149m), ("Platinum Member", 299m)], offer.Tiers.Select(t => (t.Name, t.MonthlyPrice)));
        Assert.Equal("50% off one wellness injection per visit", offer.Tiers[0].Benefits[2].Describe());
    }
}
