using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Logging;

namespace Neelam.Campaigns.Claude;

/// <summary>
/// The <c>Proofread</c> settings. <see cref="Provider"/> chooses the route: <c>Anthropic</c>, the
/// Anthropic API on the owner's own credits (owner, 2026-10-09/10: "For now I will donate my
/// credits"), or <c>Foundry</c>, Microsoft Foundry in the Azure subscription, still the intended
/// production route; unset, the proofread is off.
/// </summary>
public sealed class ProofreadOptions
{
    public const string Section = "Proofread";

    /// <summary>The one setting allowed to hold a key, as App Service resolves its Key Vault reference.</summary>
    public const string KeySetting = "Proofread:AnthropicApiKey";

    public const string AnthropicProvider = "Anthropic";

    public const string FoundryProvider = "Foundry";

    public string? Provider { get; set; }

    /// <summary>
    /// The Anthropic API key, which reaches the app only through App Service's Key Vault reference
    /// (<c>@Microsoft.KeyVault(SecretUri=...)</c>), resolved by App Service with the site's identity.
    /// Never in a file, and <c>CredentialGuard</c> refuses it anywhere else.
    /// </summary>
    public string? AnthropicApiKey { get; set; }

    public string Model { get; set; } = ClaudeProofreader.DefaultModel;

    /// <summary>low, medium or high: how hard the model thinks. Medium is Opus 5.5's own default, set explicitly.</summary>
    public string Effort { get; set; } = "medium";

    /// <summary>The most an answer may run to, thinking included: room to think and give the findings.</summary>
    public int MaxTokens { get; set; } = 16000;

    public int MaxPerClientPerDay { get; set; } = DailyProofreadAllowance.Default;
}

/// <summary>
/// The proofread as configured, checked at startup: its instructions file (always, so a bad file stops
/// the app even while the proofread is off), whether it is on, and how to make the proofreader.
/// </summary>
public sealed class ProofreadSetup
{
    private readonly ProofreadOptions _options;

    private ProofreadSetup(ProofreadOptions options, ProofreadInstructions instructions, ProofreadSwitch on, ProofreadRequestSettings settings)
    {
        _options = options;
        Instructions = instructions;
        Switch = on;
        Settings = settings;
    }

    public ProofreadInstructions Instructions { get; }

    public ProofreadSwitch Switch { get; }

    public ProofreadRequestSettings Settings { get; }

    public int MaxPerClientPerDay => _options.MaxPerClientPerDay;

    /// <summary>
    /// The setup from the settings and the shipped instructions. Refuses to start on a setting that
    /// is wrong rather than missing: an unknown provider or effort, a limit below one, or Foundry,
    /// which is not built yet. A missing or unresolved key leaves the proofread off, with the reason.
    /// </summary>
    /// <exception cref="InvalidOperationException">A setting is wrong.</exception>
    public static ProofreadSetup From(ProofreadOptions options, ProofreadInstructions instructions)
    {
        var effort = options.Effort?.Trim().ToLowerInvariant() switch
        {
            "low" => Effort.Low,
            "medium" => Effort.Medium,
            "high" => Effort.High,
            _ => throw new InvalidOperationException($"Proofread:Effort must be low, medium or high, not '{options.Effort}'."),
        };
        if (options.MaxTokens is < 1024 or > 64000)
            throw new InvalidOperationException("Proofread:MaxTokens must be between 1024 and 64000.");
        if (options.MaxPerClientPerDay < 1)
            throw new InvalidOperationException($"{DailyProofreadAllowance.Setting} must be at least 1.");
        if (string.IsNullOrWhiteSpace(options.Model))
            throw new InvalidOperationException("Proofread:Model must name a model.");

        var provider = options.Provider?.Trim();
        var settings = new ProofreadRequestSettings(options.Model.Trim(), effort, options.MaxTokens,
            ServerSideFallback: provider == ProofreadOptions.AnthropicProvider);
        ProofreadSwitch on = provider switch
        {
            null or "" => ProofreadSwitch.Off,
            ProofreadOptions.AnthropicProvider => string.IsNullOrWhiteSpace(options.AnthropicApiKey)
                ? new(false, "Proofread:AnthropicApiKey is not set.")
                : KeyVaultReference.LooksLikeOne(options.AnthropicApiKey)
                    ? new(false, "Proofread:AnthropicApiKey is a Key Vault reference App Service has not resolved: check the secret exists and the site can read it.")
                    : new(true),
            ProofreadOptions.FoundryProvider => throw new InvalidOperationException(
                "Proofread:Provider is Foundry, which is not built yet: it needs the Foundry resource, its role for the site, " +
                "and the Anthropic.Foundry client. Use Anthropic, or leave it unset."),
            _ => throw new InvalidOperationException($"Proofread:Provider must be {ProofreadOptions.AnthropicProvider} or {ProofreadOptions.FoundryProvider}, not '{provider}'."),
        };
        return new ProofreadSetup(options, instructions, on, settings);
    }

    /// <summary>The proofreader for the configured route; only when <see cref="Switch"/> is on.</summary>
    /// <param name="photos">The fetcher over the app's one photo client (<see cref="SquarePhotoFetcher.HttpClientName"/>).</param>
    public IProofreader Create(SquarePhotoFetcher photos, ILogger<ClaudeProofreader> log)
    {
        if (!Switch.IsOn) throw new InvalidOperationException("The AI proofread is not switched on: " + Switch.Why);
        var client = new AnthropicClient
        {
            ApiKey = _options.AnthropicApiKey,
            MaxRetries = 2,
            Timeout = TimeSpan.FromMinutes(3),
        };
        return new ClaudeProofreader(client, photos, Instructions, Settings, log);
    }
}

/// <summary>Stands in while the proofread is off: every proofread says it is not switched on.</summary>
public sealed class ProofreadNotSwitchedOn : IProofreader
{
    public Task<ProofreadResult> ProofreadAsync(Campaign campaign, BusinessContext? business, CancellationToken cancellationToken = default) =>
        Task.FromException<ProofreadResult>(new ProofreadUnavailableException("The AI proofread is not switched on yet."));
}
