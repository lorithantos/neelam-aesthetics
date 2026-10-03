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

    // One deployment serves every client: a container per name in the clients list, laid out the
    // same way, plus the shared settings container. Which client a request reaches is decided in
    // code, so nothing here names "the" client.
    [Fact]
    public void Each_listed_client_gets_its_own_container()
    {
        Assert.Matches(new Regex(@"param clients string\[\]"), Bicep);
        Assert.Matches(new Regex(@"for client in clients: \{\s*parent: blobService\s*name: client\s"), Bicep);
        Assert.Matches(new Regex(@"parent: blobService\s*name: 'settings'\s"), Bicep);
        Assert.DoesNotContain("Storage__Client", Bicep);
        Assert.DoesNotContain("clientName", Bicep);
    }

    // Data access belongs to the app's identity alone, scoped to each container and table, never
    // the account: a container left out of the clients list is out of the app's reach, and no
    // person holds standing data access (development uses a separate test deployment).
    [Fact]
    public void Only_the_app_holds_data_access_and_only_where_listed()
    {
        Assert.DoesNotContain("developerPrincipalId", Bicep);
        Assert.Equal(
            Regex.Matches(Bicep, @"principalId: ").Count,
            Regex.Matches(Bicep, @"principalId: site\.identity\.principalId").Count);

        string[] dataScopes = Regex.Matches(Bicep,
                @"scope: (\S+)\s*properties: \{\s*roleDefinitionId: (?:blobDataContributor|tableDataContributor)")
            .Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["clientContainers[i]", "settingsContainer", "tables[i]"], dataScopes);
    }

    // Permissive prototype access is refused in Production by the app; this keeps the real
    // deployment in Production and lets only the test deployment say otherwise.
    [Fact]
    public void Only_the_test_deployment_leaves_production()
    {
        Assert.Matches(new Regex(@"param environmentName string = 'Production'"), Bicep);
        Assert.Matches(new Regex(@"name: 'ASPNETCORE_ENVIRONMENT'\s*value: environmentName\s*\}"), Bicep);
        Assert.DoesNotContain("environmentName", File.ReadAllText(Path.Combine(Root, "infra", "main.bicepparam")));
        Assert.Contains("param environmentName = 'Test'", File.ReadAllText(Path.Combine(Root, "infra", "test.bicepparam")));
    }

    // The prototype's fixed caller exists only where prototype access may run: the setting naming
    // its client is written on the test deployment alone, and nowhere else.
    [Fact]
    public void Only_the_test_deployment_names_a_prototype_client()
    {
        Assert.Matches(new Regex(@"environmentName == 'Test' \? \[\s*\{\s*name: 'Prototype__Client'\s*value: clients\[0\]\s*\}\s*\] : \[\]"), Bicep);
        Assert.Single(Regex.Matches(Bicep, "Prototype__Client"));
    }

    // Every client a deployment lists must be a name the app will accept: a container name's shape,
    // and never a shared container such as settings.
    [Theory]
    [InlineData("main.bicepparam", "neelam-aesthetics")]
    [InlineData("test.bicepparam", "test-salon-one")]
    public void Parameter_files_list_valid_clients(string file, string expected)
    {
        var text = File.ReadAllText(Path.Combine(Root, "infra", file));
        var list = Regex.Match(text, @"param clients = \[(.*?)\]", RegexOptions.Singleline).Groups[1].Value;
        var clients = Regex.Matches(list, @"'([^']*)'").Select(m => m.Groups[1].Value).ToList();

        Assert.Contains(expected, clients);
        Assert.All(clients, c => Assert.Equal(c, new Neelam.Campaigns.Storage.ClientName(c).Value));
    }

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
