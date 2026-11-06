namespace Neelam.Campaigns.Tests;

/// <summary>
/// The block-by-block compare of two drafts (owner, 2026-10-31, answer 7a/b/c: one view for two saves,
/// the form against its last save, and the campaign against a newer template): blocks added, removed,
/// moved and changed, which fields changed with the old and new text, the subject and preheader, an
/// offer's and a promotion's parts as fields, and the template's fixed text said as such. Pure: no page,
/// no storage.
/// </summary>
public class CampaignComparisonTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 6, 18, 0, 0, TimeSpan.Zero);

    // A draft as it would be read back from a save: a copy that shares nothing with the original.
    private static CampaignDraft Copy(CampaignDraft draft) => CampaignJson.DeserializeDraft(CampaignJson.SerializeDraft(draft));

    // The finished membership email brought to a template made of Membership's blocks, changed.
    private static CampaignDraft Through(CampaignDraft draft, Func<IReadOnlyList<TemplateBlock>, IEnumerable<TemplateBlock>> change) =>
        TemplateUpdate.Of(draft, new CampaignTemplate("Membership announcement", change(DraftFixtures.Membership.Blocks).ToList()),
            Guid.NewGuid(), At).Draft;

    [Fact]
    public void Two_drafts_alike_have_no_changes()
    {
        var saved = DraftFixtures.Finished();

        var compare = CampaignComparison.Of(saved, Copy(saved));

        Assert.False(compare.HasChanges);
        Assert.Empty(compare.Email);
        Assert.Empty(compare.Blocks);
    }

    [Fact]
    public void The_subject_preheader_and_label_are_compared_as_the_email_s_own_fields()
    {
        var saved = DraftFixtures.Finished();
        var now = Copy(saved);
        now.Subject.Set("WE'RE TURNING ONE TODAY!");
        now.Preheader.Set("A year of you");
        now.Label = "Beauty Bank -- first send";

        var compare = CampaignComparison.Of(saved, now);

        Assert.True(compare.HasChanges);
        Assert.Empty(compare.Blocks);
        Assert.Collection(compare.Email,
            f => Assert.Equal(new FieldChange("Subject line", saved.Subject.Value, "WE'RE TURNING ONE TODAY!"), f),
            f => Assert.Equal(new FieldChange("Preheader", null, "A year of you"), f),
            f => Assert.Equal(new FieldChange("Label", null, "Beauty Bank -- first send"), f));
    }

    [Fact]
    public void A_changed_block_names_the_field_with_its_old_and_new_text()
    {
        var saved = DraftFixtures.Finished();
        var now = Copy(saved);
        now.Text("Headline").Set("WE'RE TURNING ONE TODAY!");
        now.Button("Call to action").Set(new CallToAction("Join now", now.Button("Call to action").Value.Url));

        var compare = CampaignComparison.Of(saved, now);

        Assert.Empty(compare.Email);
        Assert.Collection(compare.Blocks,
            headline =>
            {
                Assert.Equal("Headline", headline.Label);
                Assert.Equal(BlockChangeKind.Kept, headline.Kind);
                Assert.False(headline.Moved);
                Assert.Null(headline.WasLabel);
                var field = Assert.Single(headline.Fields);
                Assert.Equal(new FieldChange("Text", saved.Text("Headline").Value, "WE'RE TURNING ONE TODAY!"), field);
                Assert.Equal("Changed", CompareWords.What(headline));
            },
            button =>
            {
                Assert.Equal("Call to action", button.Label);
                // Only the field that changed: the link is as it was.
                var field = Assert.Single(button.Fields);
                Assert.Equal(new FieldChange("Button text", saved.Button("Call to action").Value.Label, "Join now"), field);
            });
    }

    [Fact]
    public void Blocks_added_and_removed_are_said_with_what_they_held()
    {
        var saved = DraftFixtures.Finished();
        var now = Through(saved, blocks => blocks.Where(b => b.Label != "Photo")
            .Append(new TemplateBlock("P.S.", BlockType.Paragraphs, Required: false)));
        ((ValueBlockDraft<IReadOnlyList<string>>)now["P.S."]).Value.Set(["See you soon.", "Bring a friend."]);

        var compare = CampaignComparison.Of(saved, now);

        var photo = Assert.Single(compare.Blocks, b => b.Label == "Photo");
        Assert.Equal(BlockChangeKind.Removed, photo.Kind);
        Assert.Equal("Removed", CompareWords.What(photo));
        Assert.Null(photo.To);
        Assert.Contains(new FieldChange("Photo", saved.Image("Photo").Value.Name, null), photo.Fields);
        Assert.All(photo.Fields, f => Assert.Null(f.After));

        var ps = Assert.Single(compare.Blocks, b => b.Label == "P.S.");
        Assert.Equal(BlockChangeKind.Added, ps.Kind);
        Assert.Equal("Added", CompareWords.What(ps));
        Assert.Null(ps.From);
        Assert.Equal(new FieldChange("Paragraphs", null, "See you soon.\n\nBring a friend."), Assert.Single(ps.Fields));

        // Nothing else moved: a block taken out ahead of others does not make them "moved".
        Assert.Equal(2, compare.Blocks.Count);
    }

    [Fact]
    public void A_block_moved_is_said_with_where_it_was_and_where_it_is_and_nothing_else_moves()
    {
        var saved = DraftFixtures.Finished();
        var now = Through(saved, blocks =>
        {
            var list = blocks.Where(b => b.Label != "Call to action").ToList();
            list.Insert(list.FindIndex(b => b.Label == "Opening") + 1, blocks.Single(b => b.Label == "Call to action"));
            return list;
        });

        var compare = CampaignComparison.Of(saved, now);

        var moved = Assert.Single(compare.Blocks);
        Assert.Equal("Call to action", moved.Label);
        Assert.True(moved.Moved);
        Assert.Empty(moved.Fields);
        // Counted as she sees the blocks: spacers, which have no card, are not counted.
        Assert.Equal((10, 5), (moved.From, moved.To));
        Assert.Equal("Moved from 10th to 5th", CompareWords.What(moved));
    }

    [Fact]
    public void A_block_moved_and_changed_says_both()
    {
        var saved = DraftFixtures.Finished();
        var now = Through(saved, blocks => blocks.Where(b => b.Label != "Headline").Append(blocks.Single(b => b.Label == "Headline")));
        now.Text("Headline").Set("ONE YEAR");

        var moved = Assert.Single(CampaignComparison.Of(saved, now).Blocks);

        Assert.Equal("Moved from 2nd to 10th · Changed", CompareWords.What(moved));
    }

    [Fact]
    public void A_block_renamed_in_the_template_is_matched_by_its_type_and_says_its_old_name()
    {
        var saved = DraftFixtures.Finished();
        var now = Through(saved, blocks => blocks.Select(b => b.Label == "Headline" ? b with { Label = "Heading" } : b));

        var renamed = Assert.Single(CampaignComparison.Of(saved, now).Blocks);

        Assert.Equal(("Heading", "Headline"), (renamed.Label, renamed.WasLabel));
        Assert.Equal(BlockChangeKind.Kept, renamed.Kind);
        Assert.Empty(renamed.Fields);
        Assert.Equal("Renamed from Headline", CompareWords.What(renamed));
    }

    [Fact]
    public void The_template_s_fixed_text_that_changed_is_said_as_such()
    {
        var saved = DraftFixtures.Finished();
        var now = Through(saved, blocks => blocks.Select(b =>
            b.Label == "Greeting" ? b with { Fixed = new GreetingBlock("Greeting", "Hello lovely 🤍") } : b));

        var greeting = Assert.Single(CampaignComparison.Of(saved, now).Blocks);

        var field = Assert.Single(greeting.Fields);
        Assert.Equal(new FieldChange("Text", saved.Text("Greeting").Value, "Hello lovely 🤍", Fixed: true), field);
        // Her own text is never said to be the template's.
        var typed = Copy(saved);
        typed.Text("Headline").Set("ONE YEAR");
        Assert.False(Assert.Single(Assert.Single(CampaignComparison.Of(saved, typed).Blocks).Fields).Fixed);
    }

    [Fact]
    public void An_offer_is_compared_part_by_part_and_tier_by_tier()
    {
        var saved = DraftFixtures.Finished();
        var now = Copy(saved);
        var offer = now.Offer("Offer");
        offer.Name.Set("The Beauty Bank");
        offer.Tiers[0].MonthlyPrice.Set(159m);
        offer.Tiers[1].AddBenefit(new Credit(50m, "your next appointment"));

        var block = Assert.Single(CampaignComparison.Of(saved, now).Blocks);

        Assert.Equal("Offer", block.Label);
        Assert.Collection(block.Fields,
            f => Assert.Equal(new FieldChange("Offer name", saved.Offer("Offer").Name.Value, "The Beauty Bank"), f),
            f => Assert.Equal(new FieldChange("Tier 1 › Price", "$149/month", "$159/month"), f),
            f =>
            {
                Assert.Equal("Tier 2 › Benefits", f.Field);
                Assert.Equal(f.Before + "\n$50 credit toward your next appointment", f.After);
            });
    }

    [Fact]
    public void Tiers_are_matched_by_name_so_reordering_them_is_only_a_new_order()
    {
        var saved = DraftFixtures.Finished();
        var now = Copy(saved);
        now.Offer("Offer").OrderByPrice(highestFirst: true);

        var block = Assert.Single(CampaignComparison.Of(saved, now).Blocks);

        Assert.Equal(new FieldChange("Tier order", "Gold Member, Platinum Member", "Platinum Member, Gold Member"), Assert.Single(block.Fields));
    }

    [Fact]
    public void A_tier_added_or_removed_is_said_whole()
    {
        var saved = DraftFixtures.Finished();
        var fewer = Copy(saved);
        fewer.Offer("Offer").RemoveTier(0);
        var more = Copy(saved);
        var diamond = more.Offer("Offer").AddTier();
        diamond.Name.Set("Diamond Member");
        diamond.MonthlyPrice.Set(499m);
        diamond.AddBenefit(new BirthdayCredit(100m));

        var removed = Assert.Single(Assert.Single(CampaignComparison.Of(saved, fewer).Blocks).Fields);
        var added = Assert.Single(Assert.Single(CampaignComparison.Of(saved, more).Blocks).Fields);

        Assert.Equal("Removed tier", removed.Field);
        Assert.StartsWith("Gold Member\n$149/month\n$25 birthday credit during your birth month", removed.Before);
        Assert.Null(removed.After);
        Assert.Equal(new FieldChange("Tier 3", null, "Diamond Member\n$499/month\n$100 birthday credit during your birth month"), added);
    }

    [Fact]
    public void A_promotion_is_compared_by_its_name_dates_and_lines()
    {
        var template = new CampaignTemplate("Filler Promo", [new TemplateBlock("Promotion", BlockType.Promotion)]);
        var saved = template.Start();
        var promotion = saved.Promotion("Promotion");
        promotion.Name.Set("Filler Promo");
        promotion.Starts.Set(new DateOnly(2026, 11, 1));
        promotion.Ends.Set(new DateOnly(2026, 12, 31));
        var line = promotion.AddTreatment();
        line.Treatment.Set("Full-Face Facial Balancing");
        line.Price.Set(3499m);
        line.RegularPrice.Set(5100m);
        var now = Copy(saved);
        now.Promotion("Promotion").Ends.Set(new DateOnly(2027, 1, 15));
        ((PricedTreatmentDraft)now.Promotion("Promotion").Lines[0]).Price.Set(3299m);
        now.Promotion("Promotion").AddBenefit().Benefit.Set(new Credit(50m, "your next appointment"));

        var block = Assert.Single(CampaignComparison.Of(saved, now).Blocks);

        Assert.Collection(block.Fields,
            f => Assert.Equal(new FieldChange("Ends", "December 31, 2026", "January 15, 2027"), f),
            f => Assert.Equal(new FieldChange("Line 1 › Promotion price", "$3,499", "$3,299"), f),
            f => Assert.Equal(new FieldChange("Line 2 › Benefit", null, "$50 credit toward your next appointment"), f));
    }

    [Fact]
    public void A_removed_block_is_listed_where_it_was()
    {
        var saved = DraftFixtures.Finished();
        var now = Through(saved, blocks => blocks.Where(b => b.Label != "Opening"));
        now.Text("Headline").Set("ONE YEAR");
        now.Button("Call to action").Set(new CallToAction("Join now", now.Button("Call to action").Value.Url));

        var labels = CampaignComparison.Of(saved, now).Blocks.Select(b => b.Label).ToList();

        Assert.Equal(["Headline", "Opening", "Call to action"], labels);
    }

    [Theory]
    [InlineData(1, "1st")]
    [InlineData(2, "2nd")]
    [InlineData(3, "3rd")]
    [InlineData(4, "4th")]
    [InlineData(11, "11th")]
    [InlineData(12, "12th")]
    [InlineData(13, "13th")]
    [InlineData(21, "21st")]
    [InlineData(22, "22nd")]
    [InlineData(23, "23rd")]
    [InlineData(111, "111th")]
    public void Places_are_said_as_ordinals(int place, string said) => Assert.Equal(said, CompareWords.Ordinal(place));
}
