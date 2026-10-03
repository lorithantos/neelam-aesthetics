using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

/// <summary>Stands in for Claude: returns fixed findings, or fails like an outage would.</summary>
internal sealed class FakeProofreader(params Finding[] findings) : IProofreader
{
    public Exception? Throws { get; init; }

    /// <summary>The business the last proofread was told about, to pin that the gate passes it on.</summary>
    public BusinessContext? LastBusiness { get; private set; }

    public Task<IReadOnlyList<Finding>> ProofreadAsync(
        Campaign campaign, BusinessContext? business, CancellationToken cancellationToken = default)
    {
        LastBusiness = business;
        return Throws is null
            ? Task.FromResult<IReadOnlyList<Finding>>(findings)
            : Task.FromException<IReadOnlyList<Finding>>(Throws);
    }

    public static FakeProofreader Clean => new();
}
