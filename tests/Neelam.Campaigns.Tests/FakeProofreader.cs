using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

/// <summary>Stands in for Claude: returns fixed findings, or fails like an outage would.</summary>
internal sealed class FakeProofreader(params Finding[] findings) : IProofreader
{
    public Exception? Throws { get; init; }

    /// <summary>How it says it dealt with the photos.</summary>
    public IReadOnlyList<PhotoCheck> Photos { get; init; } = [];

    /// <summary>The business the last proofread was told about, to pin that the gate passes it on.</summary>
    public BusinessContext? LastBusiness { get; private set; }

    /// <summary>The campaigns it was asked to read, in order.</summary>
    public List<Campaign> Read { get; } = [];

    public Task<ProofreadResult> ProofreadAsync(
        Campaign campaign, BusinessContext? business, CancellationToken cancellationToken = default)
    {
        LastBusiness = business;
        Read.Add(campaign);
        return Throws is null
            ? Task.FromResult(new ProofreadResult(findings, Photos))
            : Task.FromException<ProofreadResult>(Throws);
    }

    public static FakeProofreader Clean => new();
}
