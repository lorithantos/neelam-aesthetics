namespace Neelam.Campaigns.Storage;

/// <summary>
/// Refuses to start the app if any stored credential is present in its configuration: a
/// connection string, an account key, a SAS token, a password. Access is by managed identity
/// only, so any of these is either a mistake or a leak. Only setting names are reported, never values.
/// A connection string is refused as a form, even one carrying no secret: it is the shape a key
/// travels in, and the owner's rule is no connection strings at all.
/// </summary>
public static class CredentialGuard
{
    private static readonly string[] SecretMarkers =
    [
        "AccountKey=",
        "SharedAccessKey=",
        "SharedAccessSignature=",
        "Password=",
        "Secret=",
        "InstrumentationKey=",
    ];

    // Connection-string keywords that are not secrets themselves.
    private static readonly string[] ConnectionStringMarkers =
    [
        "UseDevelopmentStorage=",
        "DefaultEndpointsProtocol=",
        "AccountName=",
        "AccountEndpoint=",
        "BlobEndpoint=",
        "Endpoint=sb://",
        "IngestionEndpoint=",
        "Data Source=",
        "Initial Catalog=",
    ];

    /// <param name="settings">Typically <c>IConfiguration.AsEnumerable()</c>.</param>
    public static void EnsureNoStoredCredentials(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        var offending = settings
            .Where(kv => !string.IsNullOrEmpty(kv.Value) && LooksLikeCredential(kv.Key, kv.Value!))
            .Select(kv => kv.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (offending.Count > 0)
            throw new InvalidOperationException(
                "Stored credentials are not allowed; this app uses managed identity only. Remove: " +
                string.Join(", ", offending));
    }

    // App Service connection-string settings arrive as CUSTOMCONNSTR_* and the like, which
    // configuration maps to ConnectionStrings:*, so the name check covers those too.
    private static bool LooksLikeCredential(string key, string value) =>
        key.Replace("_", "").Contains("ConnectionString", StringComparison.OrdinalIgnoreCase)
        || SecretMarkers.Concat(ConnectionStringMarkers).Any(m => value.Contains(m, StringComparison.OrdinalIgnoreCase))
        || IsSasUrl(value);

    private static bool IsSasUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Query.Contains("sig=", StringComparison.OrdinalIgnoreCase);
}
