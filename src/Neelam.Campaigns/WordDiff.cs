using System.Text.RegularExpressions;

namespace Neelam.Campaigns;

/// <summary>One run of a field's text, as the compare shows it: changed (taken out, or put in) or as it was.</summary>
public sealed record WordPiece(string Text, bool Changed);

/// <summary>
/// The words of a changed field picked out (owner, 2026-10-31, answer 7a: "changes highlighted"): in
/// the old text, what went; in the new, what came; the rest as it was. Words and the spaces between
/// them are compared as they stand, case included. Only where that is cheap and clear: a text too long
/// to compare word by word is shown whole, all of it marked, rather than slowly or in a mess.
/// </summary>
/// <param name="Before">The old text in runs; empty when there was none.</param>
/// <param name="After">The new text in runs; empty when there is none.</param>
public sealed record WordDiff(IReadOnlyList<WordPiece> Before, IReadOnlyList<WordPiece> After)
{
    // Words and the runs of space between them.
    private static readonly Regex Tokens = new(@"\s+|\S+", RegexOptions.CultureInvariant);

    // The most word-against-word comparisons made for one field: about 500 words on each side.
    private const long MostCompared = 1_000_000;

    public static WordDiff Of(string? before, string? after)
    {
        var b = Split(before);
        var a = Split(after);
        if ((long)b.Length * a.Length > MostCompared)
            return new WordDiff(Whole(before), Whole(after));

        // lcs[i, j]: how many tokens before[i..] and after[j..] have in common, in order.
        var lcs = new int[b.Length + 1, a.Length + 1];
        for (var i = b.Length - 1; i >= 0; i--)
            for (var j = a.Length - 1; j >= 0; j--)
                lcs[i, j] = b[i] == a[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        // Walked from the start, taking a common token as soon as both sides have it, so what changed
        // starts where the change does rather than at a space before it.
        var keptBefore = new bool[b.Length];
        var keptAfter = new bool[a.Length];
        for (int i = 0, j = 0; i < b.Length && j < a.Length;)
        {
            if (b[i] == a[j])
            {
                keptBefore[i++] = true;
                keptAfter[j++] = true;
            }
            else if (lcs[i + 1, j] >= lcs[i, j + 1]) i++;
            else j++;
        }
        return new WordDiff(Pieces(b, keptBefore), Pieces(a, keptAfter));
    }

    private static string[] Split(string? text) =>
        string.IsNullOrEmpty(text) ? [] : Tokens.Matches(text).Select(m => m.Value).ToArray();

    private static IReadOnlyList<WordPiece> Whole(string? text) => string.IsNullOrEmpty(text) ? [] : [new WordPiece(text, true)];

    private static IReadOnlyList<WordPiece> Pieces(string[] tokens, bool[] kept)
    {
        var changed = kept.Select(k => !k).ToArray();
        // A space left between two changed words goes with them, so a run of new words reads as one.
        for (var i = 1; i < tokens.Length - 1; i++)
            if (!changed[i] && string.IsNullOrWhiteSpace(tokens[i]) && changed[i - 1] && changed[i + 1])
                changed[i] = true;

        var pieces = new List<WordPiece>();
        for (var i = 0; i < tokens.Length;)
        {
            var start = i;
            while (i < tokens.Length && changed[i] == changed[start]) i++;
            pieces.Add(new WordPiece(string.Concat(tokens[start..i]), changed[start]));
        }
        return pieces;
    }
}
