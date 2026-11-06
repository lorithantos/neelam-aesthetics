namespace Neelam.Campaigns.Tests;

/// <summary>
/// The words the compare highlights in a changed field (owner, 2026-10-31, answer 7a: "changes
/// highlighted"): what went from the old text and what came into the new, the rest as it was, and
/// nothing picked out where that would not be cheap or clear.
/// </summary>
public class WordDiffTests
{
    private static string Marked(IEnumerable<WordPiece> pieces) =>
        string.Concat(pieces.Select(p => p.Changed ? $"[{p.Text}]" : p.Text));

    [Fact]
    public void A_word_changed_is_picked_out_on_each_side_and_the_rest_is_as_it_was()
    {
        var diff = WordDiff.Of("Our new hours start today", "Our longer hours start today");

        Assert.Equal("Our [new] hours start today", Marked(diff.Before));
        Assert.Equal("Our [longer] hours start today", Marked(diff.After));
    }

    [Fact]
    public void Words_added_and_taken_out_together_are_one_piece_with_the_space_between()
    {
        var diff = WordDiff.Of("Join us this week", "Join us for the whole of this week");

        Assert.Equal("Join us this week", Marked(diff.Before));
        Assert.Equal("Join us [for the whole of ]this week", Marked(diff.After));
    }

    [Fact]
    public void A_space_left_between_two_changed_words_goes_with_them()
    {
        var diff = WordDiff.Of("Join us now", "Meet them now");

        Assert.Equal("[Join us] now", Marked(diff.Before));
        Assert.Equal("[Meet them] now", Marked(diff.After));
    }

    [Fact]
    public void Text_where_there_was_none_is_all_new()
    {
        var diff = WordDiff.Of(null, "Bring a friend.");

        Assert.Empty(diff.Before);
        Assert.Equal("[Bring a friend.]", Marked(diff.After));
    }

    [Fact]
    public void Text_alike_has_nothing_picked_out()
    {
        var diff = WordDiff.Of("Same words", "Same words");

        Assert.Equal("Same words", Marked(diff.Before));
        Assert.Equal("Same words", Marked(diff.After));
    }

    [Fact]
    public void Text_too_long_to_compare_cheaply_is_shown_whole_with_nothing_picked_out_word_by_word()
    {
        var before = string.Join(" ", Enumerable.Range(0, 800).Select(i => $"w{i}"));
        var after = before + " more";

        var diff = WordDiff.Of(before, after);

        Assert.Equal($"[{before}]", Marked(diff.Before));
        Assert.Equal($"[{after}]", Marked(diff.After));
    }
}
