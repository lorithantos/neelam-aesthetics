using Neelam.Campaigns;

namespace Neelam.Campaigns.Tests;

/// <summary>Stands in for Claude: returns fixed findings, or fails like an outage would.</summary>
internal sealed class FakeProofreader(params Finding[] findings) : IProofreader
{
    public Exception? Throws { get; init; }

    public Task<IReadOnlyList<Finding>> ProofreadAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
        Throws is null
            ? Task.FromResult<IReadOnlyList<Finding>>(findings)
            : Task.FromException<IReadOnlyList<Finding>>(Throws);

    public static FakeProofreader Clean => new();
}
