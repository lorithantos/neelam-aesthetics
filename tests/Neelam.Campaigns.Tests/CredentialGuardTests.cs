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
              ("Storage:Container", "campaigns"),
              ("Logging:LogLevel:Default", "Information"),
              ("AllowedHosts", "*"));
    }

    // No secret in any of these, and still refused: the rule is no connection strings at all.
    [Theory]
    [InlineData("Storage:Connection", "UseDevelopmentStorage=true")]
    [InlineData("Storage:Connection", "BlobEndpoint=https://x.blob.core.windows.net/")]
    [InlineData("Bus", "Endpoint=sb://x.servicebus.windows.net/")]
    [InlineData("Cosmos", "AccountEndpoint=https://x.documents.azure.com:443/")]
    [InlineData("Monitoring", "IngestionEndpoint=https://westus-0.in.applicationinsights.azure.com/")]
    [InlineData("Sql", "Data Source=x.database.windows.net;Initial Catalog=neelam")]
    [InlineData("Blobs_ConnectionString", "https://x.blob.core.windows.net/")]
    [InlineData("APPLICATIONINSIGHTS_CONNECTION_STRING", "anything")]
    public void Any_connection_string_stops_startup(string key, string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Check((key, value)));

        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void An_app_configuration_secret_stops_startup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Check(("Config", "Endpoint=https://x.azconfig.io;Id=abc;Secret=xyz")));

        Assert.Contains("Config", ex.Message);
    }

    [Theory]
    [InlineData("ConnectionStrings:Blobs", "UseDevelopmentStorage=true")]
    [InlineData("Storage:Connection", "DefaultEndpointsProtocol=https;AccountName=x;AccountKey=abc==")]
    [InlineData("Storage:BlobServiceUri", "https://x.blob.core.windows.net/?sv=2024-01-01&sig=abc")]
    [InlineData("APPLICATIONINSIGHTS_CONNECTION_STRING", "InstrumentationKey=00000000-0000-0000-0000-000000000000")]
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

    [Fact]
    public void Blob_backend_refuses_a_sas_address()
    {
        Assert.Throws<ArgumentException>(() => new AzureBlobBackend(
            new Uri("https://x.blob.core.windows.net/?sv=2024-01-01&sig=abc"), "campaigns", new NoCredential()));
    }

    private sealed class NoCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext r, CancellationToken c) =>
            throw new NotSupportedException();

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext r, CancellationToken c) =>
            throw new NotSupportedException();
    }
}
