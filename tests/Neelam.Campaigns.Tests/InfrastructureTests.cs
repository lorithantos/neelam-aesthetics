using System.Text.RegularExpressions;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Pins the security and retention settings in the Bicep template, so a later edit cannot
/// quietly turn keys back on or start keeping deleted saves.
/// </summary>
public class InfrastructureTests
{
    internal static readonly string Root = FindRoot();
    private static readonly string Bicep = File.ReadAllText(Path.Combine(Root, "infra", "main.bicep"));

    [Theory]
    [InlineData("allowSharedKeyAccess: false")]
    [InlineData("allowBlobPublicAccess: false")]
    [InlineData("isVersioningEnabled: false")]
    [InlineData("type: 'SystemAssigned'")]
    [InlineData("disableLocalAuth: true")]   // Log Analytics: Entra ingestion only
    [InlineData("DisableLocalAuth: true")]   // Application Insights: Entra ingestion only
    [InlineData("3913510d-42f4-4e42-8a64-420c390055eb")]   // Monitoring Metrics Publisher for the site
    public void Template_sets(string setting) => Assert.Contains(setting, Bicep);

    // Filled in from the resource at deploy time, so the value never sits in the repository.
    [Fact]
    public void Monitoring_connection_string_comes_from_the_resource() =>
        Assert.Matches(new Regex(
            @"name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'\s*value: insights\.properties\.ConnectionString\s*\}"), Bicep);

    [Theory]
    [InlineData("deleteRetentionPolicy")]
    [InlineData("containerDeleteRetentionPolicy")]
    [InlineData("changeFeed")]
    [InlineData("restorePolicy")]
    public void Nothing_keeps_a_deleted_save(string feature) =>
        Assert.Matches(new Regex($@"\b{feature}: \{{\s*enabled: false\s*\}}"), Bicep);

    [Theory]
    [InlineData("listKeys(")]
    [InlineData("connectionStrings")]
    [InlineData("AccountKey")]
    [InlineData("@secure()")]
    [InlineData("diagnosticSettings")]   // storage logs would keep the names of deleted saves
    public void Template_never_uses(string text) => Assert.DoesNotContain(text, Bicep);

    [Theory]
    [InlineData("8c6a50c6-9ffd-4ae7-986f-5fa6111f9a54")] // storage accounts: no shared key access
    [InlineData("199d5677-e4d9-4264-9465-efe1839c06bd")] // Application Insights: Entra ingestion only
    [InlineData("e15effd4-2278-4c65-a0da-4d6f6d1890e2")] // Log Analytics: Entra ingestion only
    public void Policy_refuses_key_authentication(string builtInPolicyId)
    {
        Assert.Contains(builtInPolicyId, Bicep);
        Assert.Contains("effect: { value: 'Deny' }", Bicep);
        Assert.DoesNotContain("DoNotEnforce", Bicep);
    }

    [Fact]
    public void Basic_publishing_credentials_are_off()
    {
        Assert.Equal(2, Regex.Matches(Bicep, @"basicPublishingCredentialsPolicies").Count);
        Assert.DoesNotContain("allow: true", Bicep);
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void App_settings_carry_no_connection_strings(string file) =>
        Assert.DoesNotContain("ConnectionStrings", File.ReadAllText(Path.Combine(Root, "src", "Neelam.Web", file)));

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Neelam.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("Could not find Neelam.sln above the test output.");
    }
}
