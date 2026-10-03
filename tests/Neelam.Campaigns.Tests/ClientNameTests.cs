using Neelam.Campaigns.Storage;

namespace Neelam.Campaigns.Tests;

public class ClientNameTests
{
    [Theory]
    [InlineData("neelam-aesthetics")]
    [InlineData("abc")]
    [InlineData("salon2")]
    public void A_container_shaped_name_is_accepted(string value) =>
        Assert.Equal(value, new ClientName(value).Value);

    // A client name is the container's name, so it takes a container name's shape and nothing else.
    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("Neelam")]
    [InlineData("neelam/aesthetics")]
    [InlineData("neelam--aesthetics")]
    [InlineData("-neelam")]
    [InlineData("neelam-")]
    [InlineData("neelam aesthetics")]
    [InlineData("..")]
    public void A_malformed_client_name_is_refused(string value) =>
        Assert.Throws<ArgumentException>(() => new ClientName(value));

    // A client named "settings" would be given the shared settings container as its own.
    [Fact]
    public void A_shared_container_name_is_refused() =>
        Assert.Throws<ArgumentException>(() => new ClientName("settings"));

    // Opening stores checks the service address once, at startup, rather than on a first request.
    [Fact]
    public void Client_stores_refuse_a_sas_address() =>
        Assert.Throws<ArgumentException>(() => new ClientStores(
            new Uri("https://x.blob.core.windows.net/?sv=2024-01-01&sig=abc"), new NoCredential(), TimeProvider.System));

    private sealed class NoCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext r, CancellationToken c) =>
            throw new NotSupportedException();

        public override ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext r, CancellationToken c) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void A_name_over_63_characters_is_refused() =>
        Assert.Throws<ArgumentException>(() => new ClientName(new string('a', 64)));
}
