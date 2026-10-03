using Microsoft.Extensions.Logging.Abstractions;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Runtime;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.PrivateAccess.Tests;

public sealed class PrivateAccessCleanupTests
{
    [Fact]
    public async Task CleanupAcknowledgesExpiredSessionsAndRetriesIndependentFailures()
    {
        var acknowledged = new List<string>();
        var snapshots = new[] { Snapshot("first"), Snapshot("unreachable"), Snapshot("last") };
        var cleanup = new PrivateAccessSessionCleanup(
            (_, _, _) => Task.FromResult<IReadOnlyList<PrivateAccessSessionCleanupSnapshot>>(snapshots),
            (id, _) =>
            {
                if (id == "unreachable")
                    throw new IOException("database unavailable");
                acknowledged.Add(id);
                return Task.FromResult<MarkPrivateAccessSessionCleanedOutcome>(new PrivateAccessSessionCleaned());
            },
            TimeProvider.System,
            NullLogger<PrivateAccessSessionCleanup>.Instance);

        await cleanup.RunAsync(CancellationToken.None);

        Assert.Equal(["first", "last"], acknowledged);
    }

    private static PrivateAccessSessionCleanupSnapshot Snapshot(string id) => new(
        id,
        "tenant-a",
        "grant-a",
        "target-a",
        "runtime-cell-a",
        "scope-a",
        "deployment-a",
        "cell-a",
        "database",
        NetworkPort.Create(5432),
        NetworkPort.Create(5432));
}
