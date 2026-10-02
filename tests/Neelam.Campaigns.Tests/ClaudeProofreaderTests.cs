using Neelam.Campaigns;
using Neelam.Campaigns.Claude;

namespace Neelam.Campaigns.Tests;

/// <summary>Parsing and trust rules for Claude's answer. No network: the JSON is given.</summary>
public class ClaudeProofreaderTests
{
    private static readonly string Preview = EditorExport.Preview(SampleCampaigns.SecondSend());

    [Fact]
    public void Error_becomes_a_blocker_with_its_excerpt()
    {
        const string json = """
            {"findings":[{"severity":"error","category":"duplicate-offer","location":"Offer",
              "excerpt":"Platinum Member","problem":"Both tiers are named Platinum Member.",
              "suggestion":"Rename tier 1, e.g. Gold Member."}]}
            """;

        var f = Assert.Single(ClaudeProofreader.ToFindings(json, Preview));

        Assert.Equal(Severity.Blocker, f.Severity);
        Assert.Equal("ai-duplicate-offer", f.Rule);
        Assert.Equal("Platinum Member", f.Excerpt);
        Assert.Contains("Suggest: Rename tier 1", f.Message);
    }

    [Fact]
    public void Excerpt_missing_from_the_email_is_downgraded_not_trusted()
    {
        const string json = """
            {"findings":[{"severity":"error","category":"spelling","location":"Opening",
              "excerpt":"recieve your gift","problem":"Misspelling.","suggestion":"receive"}]}
            """;

        var f = Assert.Single(ClaudeProofreader.ToFindings(json, Preview));

        Assert.Equal(Severity.Warning, f.Severity);
        Assert.Contains("Excerpt not found", f.Message);
    }

    [Fact]
    public void Empty_findings_is_a_clean_proofread()
    {
        Assert.Empty(ClaudeProofreader.ToFindings("""{"findings":[]}""", Preview));
    }
}
