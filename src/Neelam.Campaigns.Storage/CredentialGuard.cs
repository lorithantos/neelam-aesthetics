namespace Neelam.Campaigns.Storage;

/// <summary>
/// Refuses to start the app if a stored secret is present in its configuration: an account key,
/// a SAS token, a password, a client secret, an Anthropic API key. Access is by managed identity
/// only, so any of these is either a mistake or a leak. Only setting names are reported, never values.
/// A connection string carrying no secret is allowed: Application Insights is addressed by one even
/// when its ingestion is Entra-only. What keeps a connection string safe is where it is stored, an
/// App Service setting and never a file in the repository; <c>RepositoryTests</c> pins that half.
/// </summary>
/// <remarks>
/// <b>The one exception</b> (owner, 2026-10-09: "For now I will donate my credits"): the AI proofread
/// calls the Anthropic API, which takes a key, until it moves to Microsoft Foundry and the site's own
/// identity. That key may reach the app in exactly one setting, <see cref="KeySetting"/>, and only
/// through Azure Key Vault: there the value must be an App Service Key Vault reference to the secret
/// <see cref="KeyVaultReference.SecretName"/> (as the app sees one App Service has not resolved), or,
/// on App Service itself, the value App Service resolved from that reference with the site's
/// identity. The Bicep writes only the reference, and the owner sets the secret in the vault, so on
/// App Service the setting holds what the vault gave. Anywhere else -- a local run, a file, an
/// environment variable on a developer's machine -- a key in that setting is refused like any other,
/// and an Anthropic key or a Key Vault reference in any other setting is refused too.
/// <para>
/// <b>App Service's copy</b> (owner, 2026-10-10: "no one should have access so long as azure itself
/// isn't leaking"): App Service also hands every app setting to the app a second time, prefixed
/// <c>APPSETTING_</c>, so the key arrives as <see cref="AppServiceCopy"/> too. That copy is the same
/// setting, and is allowed only on App Service, and only while it holds exactly what
/// <see cref="KeySetting"/> holds or the reference itself. Off App Service, or with any other value, it
/// is refused; an <c>APPSETTING_</c> copy of any other setting is judged like any other setting.
/// </para>
/// </remarks>
public static class CredentialGuard
{
    /// <summary>The one setting that may carry a secret, from Key Vault.</summary>
    public const string KeySetting = "Proofread:AnthropicApiKey";

    /// <summary>App Service sets this on every site; it is how the guard knows App Service resolved the reference.</summary>
    public const string AppServiceMarker = "WEBSITE_SITE_NAME";

    /// <summary>
    /// The copy of <see cref="KeySetting"/> App Service puts in the environment beside it, as .NET reads
    /// <c>APPSETTING_Proofread__AnthropicApiKey</c>; the <c>__</c> spelling is the same setting.
    /// </summary>
    public const string AppServiceCopy = "APPSETTING_" + KeySetting;

    private static readonly string[] SecretMarkers =
    [
        "AccountKey=",
        "SharedAccessKey=",
        "SharedAccessSignature=",
        "Password=",
        "Secret=",
        // Anthropic's API keys begin so.
        "sk-ant-",
    ];

    /// <param name="settings">Typically <c>IConfiguration.AsEnumerable()</c>.</param>
    public static void EnsureNoStoredCredentials(IEnumerable<KeyValuePair<string, string?>> settings)
    {
        var all = settings.ToList();
        var onAppService = all.Any(kv =>
            string.Equals(kv.Key, AppServiceMarker, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value));

        var key = all.LastOrDefault(kv => string.Equals(kv.Key, KeySetting, StringComparison.OrdinalIgnoreCase)).Value;

        var offending = all
            .Where(kv => !string.IsNullOrEmpty(kv.Value) && Refused(kv.Key, kv.Value!, onAppService, key))
            .Select(kv => kv.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (offending.Count > 0)
            throw new InvalidOperationException(
                "Stored credentials are not allowed; this app uses managed identity only, and the AI proofread's key " +
                $"only through Key Vault, in {KeySetting} (on App Service also its {AppServiceCopy} copy, holding the same). " +
                "Remove: " + string.Join(", ", offending));
    }

    private static bool Refused(string name, string value, bool onAppService, string? key)
    {
        if (string.Equals(name, KeySetting, StringComparison.OrdinalIgnoreCase))
            return !(KeyVaultReference.Is(value) || onAppService);
        if (IsAppServiceCopy(name))
            return !(onAppService && (string.Equals(value, key, StringComparison.Ordinal) || KeyVaultReference.Is(value)));
        return LooksLikeCredential(value) || KeyVaultReference.LooksLikeOne(value);
    }

    private static bool IsAppServiceCopy(string name) =>
        string.Equals(name.Replace("__", ":", StringComparison.Ordinal), AppServiceCopy, StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeCredential(string value) =>
        SecretMarkers.Any(m => value.Contains(m, StringComparison.OrdinalIgnoreCase))
        || IsSasUrl(value);

    private static bool IsSasUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Query.Contains("sig=", StringComparison.OrdinalIgnoreCase);
}
