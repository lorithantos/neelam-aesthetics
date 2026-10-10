namespace Neelam.Campaigns;

/// <summary>
/// The AI proofread could not give an answer: it was refused, cut off, unreadable or unreachable.
/// The message says so in plain words for the person at the page, and never holds the email's text
/// or a credential.
/// </summary>
public sealed class ProofreadUnavailableException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

/// <summary>
/// Whether the AI proofread is switched on for this app: a provider is configured and its key has
/// reached the app (owner, 2026-10-09/10: the Anthropic API, the key from Key Vault). Pages offer the
/// proofread only when it is; otherwise they say it is not switched on yet.
/// </summary>
/// <param name="Why">When off, why, for the log: never shown with a credential in it.</param>
public sealed record ProofreadSwitch(bool IsOn, string? Why = null)
{
    public static readonly ProofreadSwitch Off = new(false, "Proofread:Provider is not set.");
}

/// <summary>
/// The cost guard in code: at most so many proofreads per client per UTC day, counting every
/// attempt, failed or refused ones too, since each may be billed. Kept in memory, so it starts again
/// when the app restarts; the hard stop is the monthly cap the owner sets in the Anthropic console
/// ($20, owner, 2026-10-09). <c>Proofread:MaxPerClientPerDay</c>, 20 unless configured.
/// </summary>
public sealed class DailyProofreadAllowance
{
    public const string Setting = "Proofread:MaxPerClientPerDay";

    public const int Default = 20;

    private readonly TimeProvider _clock;
    private readonly Dictionary<string, (DateOnly Day, int Used)> _used = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public DailyProofreadAllowance(int perClientPerDay, TimeProvider clock)
    {
        if (perClientPerDay < 1)
            throw new ArgumentOutOfRangeException(nameof(perClientPerDay), perClientPerDay,
                $"{Setting} must be at least 1; leave the proofread switched off to have none.");
        PerClientPerDay = perClientPerDay;
        _clock = clock;
    }

    public int PerClientPerDay { get; }

    /// <summary>Takes one proofread for <paramref name="client"/> today, or false when today's are used up.</summary>
    public bool TryTake(string client)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        lock (_gate)
        {
            var used = _used.TryGetValue(client, out var u) && u.Day == today ? u.Used : 0;
            if (used >= PerClientPerDay) return false;
            _used[client] = (today, used + 1);
            return true;
        }
    }
}

/// <summary>
/// The one secret the app may be given (owner, 2026-10-09: the Anthropic API key, for now): only as an
/// App Service Key Vault reference to the secret named <see cref="SecretName"/>, which App Service
/// resolves with the site's own identity. This says whether a setting's value is such a reference.
/// </summary>
public static partial class KeyVaultReference
{
    /// <summary>The secret the owner creates in the deployment's vault.</summary>
    public const string SecretName = "anthropic-api-key";

    /// <summary>
    /// True for <c>@Microsoft.KeyVault(SecretUri=https://{vault}.vault.azure.net/secrets/anthropic-api-key/)</c>,
    /// with or without a version or the closing slash: a pointer to the secret, never the secret.
    /// </summary>
    public static bool Is(string? value) => value is not null && Reference().IsMatch(value.Trim());

    /// <summary>
    /// True for any App Service Key Vault reference, to any secret: as the app sees one App Service has
    /// not resolved, or a pointer to a secret other than the one allowed.
    /// </summary>
    public static bool LooksLikeOne(string? value) =>
        value is not null && value.TrimStart().StartsWith("@Microsoft.KeyVault(", StringComparison.OrdinalIgnoreCase);

    [System.Text.RegularExpressions.GeneratedRegex(
        @"^@Microsoft\.KeyVault\(SecretUri=https://[a-z0-9-]{3,24}\.vault\.azure\.net/secrets/anthropic-api-key(/[0-9a-f]{32})?/?\)$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Reference();
}

/// <summary>What a proofread found, and how it dealt with each of the email's photos.</summary>
public sealed record ProofreadResult(IReadOnlyList<Finding> Findings, IReadOnlyList<PhotoCheck> Photos)
{
    public static ProofreadResult Of(params Finding[] findings) => new(findings, []);
}

/// <summary>How the proofread dealt with one photo, in words for her.</summary>
/// <param name="Name">The photo's name in her image library.</param>
/// <param name="Note">
/// "Read now.", "Read before; its reading was used.", or why it could not be checked, such as
/// "Couldn't check this photo: not a Square address."
/// </param>
/// <param name="Checked">True when the proofread saw the photo or its earlier reading.</param>
public sealed record PhotoCheck(string Name, string Note, bool Checked);

/// <summary>
/// What the AI read in one photo (owner, 2026-10-10: "we should track if we've seen them before"):
/// its words, a line on what it shows, and any offer, price or date it states. Kept per client by a
/// hash of the image's bytes, so an unchanged photo is not sent again, and a changed one is read anew.
/// </summary>
public sealed record PhotoReading(string Words, string Shows, string Offers);

/// <summary>
/// A client's photo readings, by the SHA-256 of each image's bytes (64 lowercase hex digits), in her
/// own container: her photos' words are her data, so never in a table, log or telemetry.
/// </summary>
public interface IPhotoReadings
{
    Task<PhotoReading?> FindAsync(string sha256, CancellationToken cancellationToken = default);

    /// <summary>Keeps a reading; one already kept for the same bytes stands.</summary>
    Task KeepAsync(string sha256, PhotoReading reading, CancellationToken cancellationToken = default);
}

/// <summary>
/// One saved version's AI proofread, as kept beside the save in the client's own container: when it
/// ran, what it found, and how it dealt with each photo. It is the client's data, as the save is (it
/// quotes her email), so it lives nowhere else: never in a table, a log, telemetry or the activity trail.
/// </summary>
public sealed record ProofreadRecord(DateTimeOffset At, IReadOnlyList<Finding> Findings, IReadOnlyList<PhotoCheck> Photos);
