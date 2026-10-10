using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

public class CredentialGuardTests
{
    private static void Check(params (string Key, string? Value)[] settings) =>
        CredentialGuard.EnsureNoStoredCredentials(
            settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));

    [Fact]
    public void Plain_addresses_are_allowed()
    {
        Check(("Storage:BlobServiceUri", "https://neelamabc.blob.core.windows.net/"),
              ("Storage:Client", "neelam-aesthetics"),
              ("Logging:LogLevel:Default", "Information"),
              ("AllowedHosts", "*"));
    }

    // No secret in any of these. A connection string is allowed where it is stored outside the
    // repository; RepositoryTests keeps one out of the repository.
    [Theory]
    [InlineData("APPLICATIONINSIGHTS_CONNECTION_STRING",
        "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://westus-0.in.applicationinsights.azure.com/")]
    [InlineData("ConnectionStrings:Blobs", "BlobEndpoint=https://x.blob.core.windows.net/")]
    [InlineData("Bus", "Endpoint=sb://x.servicebus.windows.net/")]
    [InlineData("Sql", "Data Source=x.database.windows.net;Initial Catalog=neelam")]
    public void A_connection_string_without_a_secret_is_allowed(string key, string value) =>
        Check((key, value));

    [Fact]
    public void An_app_configuration_secret_stops_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Check(("Config", "Endpoint=https://x.azconfig.io;Id=abc;Secret=xyz")));

        Assert.Contains("Config", ex.Message);
    }

    [Theory]
    [InlineData("Storage:Connection", "DefaultEndpointsProtocol=https;AccountName=x;AccountKey=abc==")]
    [InlineData("Bus", "Endpoint=sb://x.servicebus.windows.net/;SharedAccessKeyName=a;SharedAccessKey=abc=")]
    [InlineData("Storage:BlobServiceUri", "https://x.blob.core.windows.net/?sv=2024-01-01&sig=abc")]
    [InlineData("Db", "Server=x;User Id=a;Password=b")]
    public void Any_stored_credential_stops_startup(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Check((key, value)));

        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void The_error_names_the_setting_but_never_shows_the_secret()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Check(("Storage:Connection", "AccountName=x;AccountKey=TOPSECRET==")));

        Assert.DoesNotContain("TOPSECRET", ex.Message);
    }

    // ---- The one exception (owner, 2026-10-09): the AI proofread's Anthropic key, from Key Vault ----

    private const string Reference = "@Microsoft.KeyVault(SecretUri=https://neelam-kv-abc123.vault.azure.net/secrets/anthropic-api-key/)";

    // Built from parts, so this file holds nothing shaped like a real key.
    private static readonly string AnthropicKey = string.Concat("sk-", "ant-", "api03-", new string('x', 40));

    [Theory]
    [InlineData(Reference)]
    [InlineData("@Microsoft.KeyVault(SecretUri=https://neelam-kv-abc123.vault.azure.net/secrets/anthropic-api-key)")]
    [InlineData("@Microsoft.KeyVault(SecretUri=https://neelam-kv-abc123.vault.azure.net/secrets/anthropic-api-key/0123456789abcdef0123456789abcdef)")]
    public void The_proofread_key_setting_may_hold_its_key_vault_reference(string value) =>
        Check((CredentialGuard.KeySetting, value));

    [Fact]
    public void The_reference_comes_in_as_app_service_writes_it_from_the_bicep() =>
        Check(("Proofread:AnthropicApiKey", Reference), ("Storage:BlobServiceUri", "https://x.blob.core.windows.net/"));

    // App Service resolves the reference with the site's identity: the app then sees the key, in that
    // one setting, on App Service alone.
    [Fact]
    public void On_app_service_the_resolved_key_is_allowed_in_that_setting_only() =>
        Check((CredentialGuard.AppServiceMarker, "neelamtest-abc"), (CredentialGuard.KeySetting, AnthropicKey));

    [Fact]
    public void Off_app_service_a_key_in_that_setting_stops_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Check((CredentialGuard.KeySetting, AnthropicKey)));

        Assert.Contains(CredentialGuard.KeySetting, ex.Message);
        Assert.DoesNotContain(AnthropicKey, ex.Message);
    }

    [Theory]
    [InlineData("anything-at-all")]
    [InlineData("@Microsoft.KeyVault(SecretUri=https://neelam-kv-abc123.vault.azure.net/secrets/storage-key/)")]
    [InlineData("@Microsoft.KeyVault(SecretUri=https://evil.example.com/secrets/anthropic-api-key/)")]
    [InlineData("@Microsoft.KeyVault(VaultName=neelam-kv;SecretName=anthropic-api-key)")]
    public void Off_app_service_only_the_reference_to_that_one_secret_is_allowed_there(string value) =>
        Assert.Throws<InvalidOperationException>(() => Check((CredentialGuard.KeySetting, value)));

    [Theory]
    [InlineData("Proofread:Key")]
    [InlineData("ANTHROPIC_API_KEY")]
    [InlineData("Logging:Note")]
    public void An_anthropic_key_anywhere_else_stops_startup_even_on_app_service(string key)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Check((CredentialGuard.AppServiceMarker, "neelamtest-abc"), (key, AnthropicKey)));

        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void A_key_vault_reference_in_any_other_setting_stops_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Check((CredentialGuard.AppServiceMarker, "neelamtest-abc"), ("Storage:Connection", Reference)));

        Assert.Contains("Storage:Connection", ex.Message);
    }

    [Fact]
    public void Every_other_secret_is_still_refused_on_app_service() =>
        Assert.Throws<InvalidOperationException>(() =>
            Check((CredentialGuard.AppServiceMarker, "neelamtest-abc"), (CredentialGuard.KeySetting, Reference),
                ("Storage:Connection", "AccountName=x;AccountKey=abc==")));

    // Every store the app opens goes through one StorageClients built at startup, so a SAS on
    // either endpoint stops the app there, before any request.
    [Theory]
    [InlineData("https://x.blob.core.windows.net/?sv=2024-01-01&sig=abc", "https://x.table.core.windows.net/")]
    [InlineData("https://x.blob.core.windows.net/", "https://x.table.core.windows.net/?sv=2024-01-01&sig=abc")]
    public void The_app_s_storage_refuses_a_sas_address(string blob, string table) =>
        Assert.Throws<ArgumentException>(() => new Janet.Azure.Storage.StorageClients(
            new Janet.Azure.Storage.StorageEndpoints(Blob: new Uri(blob), Table: new Uri(table)), new NoCredential()));

    private sealed class NoCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext r, CancellationToken c) =>
            throw new NotSupportedException();

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext r, CancellationToken c) =>
            throw new NotSupportedException();
    }
}
