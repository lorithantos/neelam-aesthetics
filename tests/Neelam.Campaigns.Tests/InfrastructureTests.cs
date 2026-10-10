using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Neelam.Campaigns.Tests;

/// <summary>
/// Pins the security and retention settings in the Bicep templates, so a later edit cannot
/// quietly turn keys back on or start keeping deleted saves. A site and its storage account are
/// each one definition (site.bicep, storage.bicep), used by main.bicep for the client's site and,
/// on the test deployment, for the staging site; the settings are pinned there once, and how
/// main.bicep wires each instance is pinned here too.
/// </summary>
public class InfrastructureTests
{
    internal static readonly string Root = FindRoot();
    private static readonly string Bicep = Infra("main.bicep");
    private static readonly string Site = Infra("site.bicep");
    private static readonly string Storage = Infra("storage.bicep");
    private static readonly string[] Templates = [Bicep, Site, Storage];

    [Theory]
    [InlineData("storage.bicep", "allowSharedKeyAccess: false")]
    [InlineData("storage.bicep", "allowBlobPublicAccess: false")]
    [InlineData("storage.bicep", "isVersioningEnabled: false")]
    [InlineData("site.bicep", "type: 'SystemAssigned'")]
    [InlineData("main.bicep", "disableLocalAuth: true")]   // Log Analytics: Entra ingestion only
    [InlineData("main.bicep", "DisableLocalAuth: true")]   // Application Insights: Entra ingestion only
    [InlineData("site.bicep", "3913510d-42f4-4e42-8a64-420c390055eb")]   // Monitoring Metrics Publisher for the site
    public void Template_sets(string file, string setting) => Assert.Contains(setting, Infra(file));

    // Every site and every storage account is an instance of the one definition, so the staging site
    // and its account carry exactly the settings pinned on site.bicep and storage.bicep: main.bicep
    // declares neither a site nor an account of its own, and grants no role itself.
    [Fact]
    public void Every_site_and_account_comes_from_the_one_definition()
    {
        Assert.Equal(["site", "stagingSite"], ModulesOf("site.bicep"));
        Assert.Equal(["stagingStorage", "storage"], ModulesOf("storage.bicep"));
        Assert.Equal(5, Regex.Matches(Bicep, @"^module ", RegexOptions.Multiline).Count);
        Assert.DoesNotContain("'Microsoft.Storage/", Bicep);
        Assert.DoesNotContain("'Microsoft.Web/sites@", Bicep);
        Assert.DoesNotContain("roleAssignments", Bicep);
        Assert.Single(Regex.Matches(Site, @"^resource \w+ 'Microsoft\.Web/sites@", RegexOptions.Multiline));
        Assert.Single(Regex.Matches(Storage, @"^resource \w+ 'Microsoft\.Storage/storageAccounts@", RegexOptions.Multiline));
    }

    // The client's site and account keep the names, and so the resource ids, they had before they
    // became modules: the move must change nothing that is deployed.
    [Fact]
    public void The_client_site_and_account_keep_their_names()
    {
        Assert.Matches(new Regex(@"var storageName = take\(toLower\('\$\{prefix\}\$\{suffix\}'\), 24\)\s"), Bicep);
        Assert.Matches(new Regex(@"var siteName = '\$\{prefix\}-\$\{suffix\}'\s"), Bicep);
        Assert.Matches(new Regex(@"var stagingStorageName = take\(toLower\('\$\{prefix\}stg\$\{suffix\}'\), 24\)\s"), Bicep);
        Assert.Matches(new Regex(@"var stagingSiteName = '\$\{prefix\}-staging-\$\{suffix\}'\s"), Bicep);
    }

    // Each site uses its own account, and each account gives its data roles to its own site alone:
    // the staging identity holds nothing on the client's account, and the client's site nothing on
    // staging's. Both sites share the plan and Application Insights; both accounts get every table.
    [Fact]
    public void Each_site_has_its_own_account_and_identity()
    {
        Assert.Equal(new Dictionary<string, string>
        {
            ["name"] = "siteName", ["location"] = "location", ["planId"] = "plan.id",
            ["environmentName"] = "environmentName", ["prototypeClient"] = "prototypeClient",
            ["storageName"] = "storageName", ["insightsName"] = "insights.name", ["proofreadKeyUri"] = "proofreadKeyUri",
        }, ModuleParams("site"));
        Assert.Equal(new Dictionary<string, string>
        {
            ["name"] = "storageName", ["location"] = "location", ["clients"] = "clients",
            ["tableNames"] = "tableNames", ["appId"] = "resourceId('Microsoft.Web/sites', siteName)",
            ["appPrincipalId"] = "site.outputs.principalId", ["developerPrincipalId"] = "developerOnAccounts",
        }, ModuleParams("storage"));
        Assert.Equal(new Dictionary<string, string>
        {
            ["name"] = "stagingSiteName", ["location"] = "location", ["planId"] = "plan.id",
            ["environmentName"] = "environmentName", ["prototypeClient"] = "stagingPrototypeClient",
            ["storageName"] = "stagingStorageName", ["insightsName"] = "insights.name", ["proofreadKeyUri"] = "proofreadKeyUri",
        }, ModuleParams("stagingSite"));
        Assert.Equal(new Dictionary<string, string>
        {
            ["name"] = "stagingStorageName", ["location"] = "location", ["clients"] = "stagingClients",
            ["tableNames"] = "tableNames", ["appId"] = "resourceId('Microsoft.Web/sites', stagingSiteName)",
            ["appPrincipalId"] = "stagingSite!.outputs.principalId", ["developerPrincipalId"] = "developerOnAccounts",
        }, ModuleParams("stagingStorage"));
    }

    // ---- The one secret: the AI proofread's Anthropic key, in Key Vault (owner, 2026-10-09) ----

    private static readonly string Vault = Infra("vault.bicep");

    // The vault's security settings, pinned: RBAC only with no access policies, purge protection,
    // nothing else of Azure's allowed to read it, the same network as the other resources.
    [Theory]
    [InlineData("enableRbacAuthorization: true")]
    [InlineData("accessPolicies: []")]
    [InlineData("enablePurgeProtection: true")]
    [InlineData("enableSoftDelete: true")]
    [InlineData("enabledForDeployment: false")]
    [InlineData("enabledForDiskEncryption: false")]
    [InlineData("enabledForTemplateDeployment: false")]
    [InlineData("publicNetworkAccess: 'Enabled'")]
    [InlineData("4633458b-17de-408a-b874-0445c86b69e6")] // Key Vault Secrets User
    public void The_vault_sets(string setting) => Assert.Contains(setting, Vault);

    // One vault, one definition; its readers are the sites' own identities with Key Vault Secrets User
    // on that vault alone, its one setter is pinned below, and no secret, key or value is ever written
    // by a template.
    [Fact]
    public void Each_site_reads_the_key_and_nothing_else_in_the_vault()
    {
        Assert.Single(Regex.Matches(Vault, @"^resource \w+ 'Microsoft\.KeyVault/vaults@", RegexOptions.Multiline));
        Assert.All(Templates.Append(Vault), t => Assert.DoesNotContain("Microsoft.KeyVault/vaults/secrets", t));
        Assert.All(Templates.Append(Vault), t => Assert.DoesNotContain("KeyVault/vaults/accessPolicies", t));
        Assert.DoesNotContain("'Microsoft.KeyVault/", Bicep);

        string[] roles = Regex.Matches(Vault, @"scope: (\w+)\s*properties: \{\s*roleDefinitionId: (\w+)\s*principalId: (\w+)\s")
            .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value}").ToArray();
        Assert.Equal([
            "vault keyVaultSecretsUser readerPrincipalId",
            "vault keyVaultSecretsUser stagingPrincipalId",
            "vault keyVaultSecretsOfficer keyOfficerPrincipalId",
        ], roles);
        Assert.Equal(3, Regex.Matches(Vault, "principalId: ").Count);
        Assert.Equal(new Dictionary<string, string>
        {
            ["name"] = "vaultName", ["location"] = "location",
            ["readerSiteName"] = "siteName", ["readerPrincipalId"] = "site.outputs.principalId",
            ["stagingSiteName"] = "staging ? stagingSiteName : ''",
            ["stagingPrincipalId"] = "staging ? stagingSite!.outputs.principalId : ''",
            ["keyOfficerPrincipalId"] = "developerOnAccounts",
        }, ModuleParams("vault"));
    }

    // The person who adds and rotates the key (owner, 2026-10-10: "I should be allowed to add a key
    // and to rotate one") holds Key Vault Secrets Officer, the smallest built-in role that can set a
    // secret, as a user, on the vault alone, and only when one is named: the developer, on the test
    // deployment (developerOnAccounts, pinned above). Production names no one; who should hold it there
    // is the owner's open decision (WIP.md).
    [Fact]
    public void Only_the_named_person_sets_the_key_and_on_the_vault_alone()
    {
        Assert.Matches(new Regex(@"param keyOfficerPrincipalId string = ''\s"), Vault);
        Assert.Matches(new Regex(
            @"var keyVaultSecretsOfficer = subscriptionResourceId\(\s*'Microsoft\.Authorization/roleDefinitions',\s*'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'\s*\)"), Vault);
        Assert.Single(Regex.Matches(Vault,
            @"^resource \w+ 'Microsoft\.Authorization/roleAssignments@2022-04-01' = if \(!empty\(keyOfficerPrincipalId\)\) \{\s*" +
            @"name: guid\(vault\.id, keyOfficerPrincipalId, keyVaultSecretsOfficer\)\s*" +
            @"scope: vault\s*" +
            @"properties: \{\s*roleDefinitionId: keyVaultSecretsOfficer\s*principalId: keyOfficerPrincipalId\s*principalType: 'User'\s*\}\s*\}",
            RegexOptions.Multiline));
        Assert.Single(Regex.Matches(Vault, @"roleDefinitionId: keyVaultSecretsOfficer\s"));
        Assert.Single(Regex.Matches(Vault, @"principalId: keyOfficerPrincipalId\s"));
        Assert.All(Templates, t => Assert.DoesNotContain("b86a8fe4-44ce-4948-aee5-eccb2c155cd7", t));
        Assert.Equal("developerOnAccounts", ModuleParams("vault")["keyOfficerPrincipalId"]);
    }

    // The key reaches each site only as a Key Vault reference to the one secret, which App Service
    // resolves with the site's own identity: the setting CredentialGuard lets it into, and no other.
    [Fact]
    public void The_site_gets_the_key_as_a_key_vault_reference_alone()
    {
        Assert.Contains("keyVaultReferenceIdentity: 'SystemAssigned'", Site);
        Assert.Matches(new Regex(@"name: 'Proofread__AnthropicApiKey'\s*value: '@Microsoft\.KeyVault\(SecretUri=\$\{proofreadKeyUri\}\)'\s"), Site);
        Assert.Single(Regex.Matches(Site, "@Microsoft.KeyVault"));
        Assert.Matches(new Regex(@"var proofreadSecretName = 'anthropic-api-key'\s"), Bicep);
        Assert.Matches(new Regex(@"var proofreadKeyUri = 'https://\$\{vaultName\}\$\{environment\(\)\.suffixes\.keyvaultDns\}/secrets/\$\{proofreadSecretName\}/'\s"), Bicep);
        Assert.Equal(Neelam.Campaigns.KeyVaultReference.SecretName, Regex.Match(Bicep, @"var proofreadSecretName = '([^']+)'").Groups[1].Value);
        Assert.Equal("Proofread__AnthropicApiKey", Neelam.Campaigns.Storage.CredentialGuard.KeySetting.Replace(":", "__"));
        // What the Bicep writes is what the guard allows, for the vault name it makes on the test deployment.
        Assert.Matches(new Regex(@"var vaultName = take\('\$\{prefix\}-kv-\$\{suffix\}', 24\)\s"), Bicep);
        Assert.True(Neelam.Campaigns.KeyVaultReference.Is(
            "@Microsoft.KeyVault(SecretUri=https://neelamtest-kv-6excvnu62r.vault.azure.net/secrets/anthropic-api-key/)"));
    }

    // The staging site exists on the test deployment alone: only test.bicepparam names its clients,
    // and the template ignores them outside Test. Production gets nothing new.
    [Fact]
    public void Only_the_test_deployment_has_a_staging_site()
    {
        Assert.Matches(new Regex(@"param stagingClients string\[\] = \[\]\s"), Bicep);
        Assert.Matches(new Regex(@"var staging = environmentName == 'Test' && !empty\(stagingClients\)\s"), Bicep);
        Assert.Single(Regex.Matches(Bicep, @"staging = "));
        Assert.Matches(new Regex(@"module stagingSite 'site\.bicep' = if \(staging\) \{"), Bicep);
        Assert.Matches(new Regex(@"module stagingStorage 'storage\.bicep' = if \(staging\) \{"), Bicep);
        Assert.Matches(new Regex(@"output stagingSiteName string = staging \? stagingSiteName : ''\s"), Bicep);

        Assert.DoesNotContain("staging", File.ReadAllText(Path.Combine(Root, "infra", "main.bicepparam")), StringComparison.OrdinalIgnoreCase);
        Assert.Matches(new Regex(@"param stagingClients = \["), File.ReadAllText(Path.Combine(Root, "infra", "test.bicepparam")));
    }

    // One deployment serves every client: a container per name in the clients list, laid out the
    // same way, plus the shared settings container. Which client a request reaches is decided in
    // code, so nothing here names "the" client.
    [Fact]
    public void Each_listed_client_gets_its_own_container()
    {
        Assert.Matches(new Regex(@"param clients string\[\]"), Bicep);
        Assert.Matches(new Regex(@"param clients string\[\]"), Storage);
        Assert.Matches(new Regex(@"for client in clients: \{\s*parent: blobService\s*name: client\s"), Storage);
        Assert.Matches(new Regex(@"parent: blobService\s*name: 'settings'\s"), Storage);
        Assert.All(Templates, t => Assert.DoesNotContain("Storage__Client", t));
        Assert.All(Templates, t => Assert.DoesNotContain("clientName", t));
    }

    // The site's data access is scoped to each container and table, never the account: a container
    // left out of the clients list is out of the site's reach. The only other principal is the
    // developer, pinned below; the site's own identity otherwise holds only its telemetry role.
    [Fact]
    public void The_app_holds_data_access_only_where_listed()
    {
        Assert.Equal(
            Regex.Matches(Storage, @"principalId: ").Count,
            Regex.Matches(Storage, @"principalId: (?:appPrincipalId|developerPrincipalId)\s").Count);

        string[] dataScopes = Regex.Matches(Storage,
                @"scope: (\S+)\s*properties: \{\s*roleDefinitionId: (?:blobDataContributor|tableDataContributor)\s*principalId: appPrincipalId\s")
            .Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["clientContainers[i]", "settingsContainer", "tables[i]"], dataScopes);

        string[] siteRoles = Regex.Matches(Site,
                @"scope: (\S+)\s*properties: \{\s*roleDefinitionId: (\w+)\s*principalId: site\.identity\.principalId\s")
            .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}").ToArray();
        Assert.Equal(["insights metricsPublisher"], siteRoles);
        Assert.Single(Regex.Matches(Site, @"principalId: "));
    }

    // The metadata tables, each with the site's table role scoped to it (tables[i], above). The
    // activity table was added on 2026-10-09 for the activity trail, and knownItems the same day for
    // each client's known items: changing this list is a live infrastructure deploy, so it is pinned
    // here and changed deliberately. Every account gets the one list (Each_site_has_its_own_account).
    [Fact]
    public void The_metadata_tables_are_exactly_these()
    {
        var list = Regex.Match(Bicep, @"var tableNames = \[(.*?)\]", RegexOptions.Singleline).Groups[1].Value;
        string[] tables = Regex.Matches(list, @"'([^']*)'").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(["clients", "supportGrants", "approvals", "activity", "knownItems"], tables);
        Assert.Matches(new Regex(@"for name in tableNames: \{\s*parent: tableService\s*name: name\s"), Storage);
        Assert.Matches(new Regex(@"for \(name, i\) in tableNames: \{\s*name: guid\(tables\[i\]\.id, appId, tableDataContributor\)\s*scope: tables\[i\]\s"), Storage);
    }

    // No person holds data access in production. On the test deployment, whose clients are made
    // up, the developer holds blob and table data access to the whole of each account; the template
    // grants it only when the environment is Test, and only the test parameter file names a developer.
    [Fact]
    public void Only_the_test_deployment_grants_the_developer_data_access()
    {
        Assert.Matches(new Regex(@"param developerPrincipalId string = ''\s"), Bicep);
        Assert.Matches(new Regex(@"var developerAccess = environmentName == 'Test' && !empty\(developerPrincipalId\)\s"), Bicep);
        Assert.Single(Regex.Matches(Bicep, @"developerAccess = "));
        Assert.Matches(new Regex(@"var developerOnAccounts = developerAccess \? developerPrincipalId : ''\s"), Bicep);
        Assert.Equal(2, Regex.Matches(Bicep, @"developerPrincipalId: ").Count);
        Assert.Equal(2, Regex.Matches(Bicep, @"developerPrincipalId: developerOnAccounts\s").Count);

        Assert.Matches(new Regex(@"param developerPrincipalId string = ''\s"), Storage);
        string[] developerRoles = Regex.Matches(Storage,
                @"= if \(!empty\(developerPrincipalId\)\) \{\s*name: [^\n]+\s*scope: storage\s*properties: \{\s*roleDefinitionId: (\w+)\s*principalId: developerPrincipalId\s*principalType: 'User'\s")
            .Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["blobDataContributor", "tableDataContributor"], developerRoles);
        Assert.Equal(developerRoles.Length, Regex.Matches(Storage, @"principalId: developerPrincipalId\s").Count);

        Assert.DoesNotContain("developerPrincipalId", File.ReadAllText(Path.Combine(Root, "infra", "main.bicepparam")));
        Assert.Matches(new Regex(@"param developerPrincipalId = '[0-9a-f-]{36}'"),
            File.ReadAllText(Path.Combine(Root, "infra", "test.bicepparam")));
    }

    // Permissive prototype access is refused in Production by the app; this keeps the real
    // deployment in Production and lets only the test deployment say otherwise.
    [Fact]
    public void Only_the_test_deployment_leaves_production()
    {
        Assert.Matches(new Regex(@"param environmentName string = 'Production'"), Bicep);
        Assert.Matches(new Regex(@"name: 'ASPNETCORE_ENVIRONMENT'\s*value: environmentName\s*\}"), Site);
        Assert.DoesNotContain("environmentName", File.ReadAllText(Path.Combine(Root, "infra", "main.bicepparam")));
        Assert.Contains("param environmentName = 'Test'", File.ReadAllText(Path.Combine(Root, "infra", "test.bicepparam")));
    }

    // The prototype's fixed caller exists only where prototype access may run: the setting naming
    // its client is written on the test deployment alone, and the deployment reports the same value.
    // The staging site works as its own first client, and exists only on the test deployment.
    [Fact]
    public void Only_the_test_deployment_names_a_prototype_client()
    {
        Assert.Matches(new Regex(@"var prototypeClient = environmentName == 'Test' \? clients\[0\] : ''"), Bicep);
        Assert.Matches(new Regex(@"var stagingPrototypeClient = stagingClients\[\?0\] \?\? ''\s"), Bicep);
        Assert.Single(Regex.Matches(Bicep, @"\bprototypeClient = "));
        Assert.Single(Regex.Matches(Bicep, @"\bstagingPrototypeClient = "));
        Assert.Matches(new Regex(@"output prototypeClient string = prototypeClient\s"), Bicep);

        Assert.Matches(new Regex(@"param prototypeClient string = ''\s"), Site);
        Assert.Matches(new Regex(@"empty\(prototypeClient\) \? \[\] : \[\s*\{\s*name: 'Prototype__Client'\s*value: prototypeClient\s*\}\s*\]"), Site);
        Assert.Single(Regex.Matches(Site, "name: 'Prototype__Client'"));
        Assert.DoesNotContain("name: 'Prototype__Client'", Bicep);
        Assert.DoesNotContain("name: 'Prototype__Client'", Storage);
    }

    // Every client a deployment lists must be a name the app will accept: a container name's shape,
    // and never a shared container such as settings.
    [Theory]
    [InlineData("main.bicepparam", "clients", "neelam-aesthetics")]
    [InlineData("test.bicepparam", "clients", "test-salon-one")]
    [InlineData("test.bicepparam", "stagingClients", "staging-salon")]
    public void Parameter_files_list_valid_clients(string file, string param, string expected)
    {
        var text = File.ReadAllText(Path.Combine(Root, "infra", file));
        var list = Regex.Match(text, $@"param {param} = \[(.*?)\]", RegexOptions.Singleline).Groups[1].Value;
        var clients = Regex.Matches(list, @"'([^']*)'").Select(m => m.Groups[1].Value).ToList();

        Assert.Contains(expected, clients);
        Assert.All(clients, c => Assert.Equal(c, new Neelam.Campaigns.Storage.ClientName(c).Value));
    }

    // Filled in from the resource at deploy time, so the value never sits in the repository.
    [Fact]
    public void Monitoring_connection_string_comes_from_the_resource() =>
        Assert.Matches(new Regex(
            @"name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'\s*value: insights\.properties\.ConnectionString\s*\}"), Site);

    // The site's storage addresses follow from its own account's name, never another's.
    [Fact]
    public void Each_site_addresses_its_own_account()
    {
        Assert.Matches(new Regex(@"name: 'Storage__BlobServiceUri'\s*value: 'https://\$\{storageName\}\.blob\.\$\{environment\(\)\.suffixes\.storage\}/'\s*\}"), Site);
        Assert.Matches(new Regex(@"name: 'Storage__TableServiceUri'\s*value: 'https://\$\{storageName\}\.table\.\$\{environment\(\)\.suffixes\.storage\}/'\s*\}"), Site);
    }

    [Theory]
    [InlineData("deleteRetentionPolicy")]
    [InlineData("containerDeleteRetentionPolicy")]
    [InlineData("changeFeed")]
    [InlineData("restorePolicy")]
    public void Nothing_keeps_a_deleted_save(string feature) =>
        Assert.Matches(new Regex($@"\b{feature}: \{{\s*enabled: false\s*\}}"), Storage);

    [Theory]
    [InlineData("listKeys(")]
    [InlineData("connectionStrings")]
    [InlineData("AccountKey")]
    [InlineData("@secure()")]
    [InlineData("diagnosticSettings")]   // storage logs would keep the names of deleted saves
    public void Template_never_uses(string text) => Assert.All(Templates, t => Assert.DoesNotContain(text, t));

    [Theory]
    [InlineData("8c6a50c6-9ffd-4ae7-986f-5fa6111f9a54")] // storage accounts: no shared key access
    [InlineData("199d5677-e4d9-4264-9465-efe1839c06bd")] // Application Insights: Entra ingestion only
    [InlineData("e15effd4-2278-4c65-a0da-4d6f6d1890e2")] // Log Analytics: Entra ingestion only
    public void Policy_refuses_key_authentication(string builtInPolicyId)
    {
        Assert.Contains(builtInPolicyId, Bicep);
        Assert.Contains("effect: { value: 'Deny' }", Bicep);
        Assert.All(Templates, t => Assert.DoesNotContain("DoNotEnforce", t));
    }

    [Fact]
    public void Basic_publishing_credentials_are_off()
    {
        Assert.Equal(2, Regex.Matches(Site, @"basicPublishingCredentialsPolicies").Count);
        Assert.Equal(2, Regex.Matches(Site, @"properties: \{\s*allow: false\s*\}").Count);
        Assert.All(Templates, t => Assert.DoesNotContain("allow: true", t));
    }

    // The staging deploy is the client site's deploy pointed at the staging site: the same build,
    // package and stamp, pushed to the app Bicep names '${prefix}-staging-${suffix}' beside
    // '${prefix}-${suffix}', and verified on that app's own /healthz.
    [Fact]
    public void The_staging_deploy_mirrors_the_client_site_deploy()
    {
        var test = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "deploy-manifest.test.json")))!.AsObject();
        var staging = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "deploy-manifest.staging.json")))!.AsObject();

        var site = (string)test["stages"]!["push"]!["app"]!;
        var stagingSite = (string)staging["stages"]!["push"]!["app"]!;
        var dash = site.IndexOf('-');
        Assert.Equal($"{site[..dash]}-staging{site[dash..]}", stagingSite);
        Assert.Equal(test["stages"]!["push"]!["resourceGroup"]!.ToJsonString(), staging["stages"]!["push"]!["resourceGroup"]!.ToJsonString());
        Assert.Equal($"https://{stagingSite}.azurewebsites.net/healthz", (string)staging["verify"]!["url"]!);
        Assert.Equal($"https://{site}.azurewebsites.net/healthz", (string)test["verify"]!["url"]!);

        Assert.Equal(test["identity"]!.ToJsonString(), staging["identity"]!.ToJsonString());
        foreach (var stage in new[] { "build", "package" })
            Assert.Equal(test["stages"]![stage]!.ToJsonString(), staging["stages"]![stage]!.ToJsonString());
        Assert.Equal(
            test["verify"]!.AsObject().Where(p => p.Key != "url").Select(p => $"{p.Key}={p.Value!.ToJsonString()}"),
            staging["verify"]!.AsObject().Where(p => p.Key != "url").Select(p => $"{p.Key}={p.Value!.ToJsonString()}"));
        Assert.Contains("deploy-manifest.test.json", (string)staging["promote"]!["hint"]!);
    }

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("appsettings.Development.json")]
    public void App_settings_carry_no_connection_strings(string file) =>
        Assert.DoesNotContain("ConnectionStrings", File.ReadAllText(Path.Combine(Root, "src", "Neelam.Web", file)));

    private static string Infra(string file) => File.ReadAllText(Path.Combine(Root, "infra", file));

    private static string[] ModulesOf(string file) =>
        Regex.Matches(Bicep, $@"^module (\w+) '{Regex.Escape(file)}'", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();

    // A module's params block as written, name to expression, so a param added, dropped or rewired
    // fails the pin.
    private static Dictionary<string, string> ModuleParams(string module)
    {
        var block = Regex.Match(Bicep.ReplaceLineEndings("\n"),
            $@"^module {module} '[^']+' = (?:if \(\w+\) )?\{{\n  name: '{module}'\n  params: \{{\n(.*?)\n  \}}\n\}}",
            RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(block.Success, $"module {module} not found in the shape this test reads");
        return block.Groups[1].Value.Split('\n')
            .Select(line => line.Trim().Split(": ", 2))
            .ToDictionary(kv => kv[0], kv => kv[1]);
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Neelam.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("Could not find Neelam.sln above the test output.");
    }
}
