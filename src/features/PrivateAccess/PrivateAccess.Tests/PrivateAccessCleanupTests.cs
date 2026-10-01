using Microsoft.Extensions.Logging.Abstractions;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Runtime;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.PrivateAccess.Tests;

public sealed class PrivateAccessCleanupTests
{
    [Fact]
    public async Task Cleanup_UsesPersistedNamespace_AndAcknowledgesOnlyCompletedDeletions()
    {
        var acknowledged = new List<string>();
        var deleted = new List<(string Session, string Namespace)>();
        var snapshots = new[]
        {
            Snapshot("never-provisioned", null),
            Snapshot("removed", "old-worker-namespace"),
            Snapshot("conflict", "old-worker-namespace"),
            Snapshot("unreachable", "another-worker-namespace"),
            Snapshot("after-failure", "another-worker-namespace")
        };
        var cleanup = new PrivateAccessSessionCleanup(
            (_, _, _) => Task.FromResult<IReadOnlyList<PrivateAccessSessionCleanupSnapshot>>(snapshots),
            (id, _) =>
            {
                acknowledged.Add(id);
                return Task.FromResult<MarkPrivateAccessSessionCleanedOutcome>(new PrivateAccessSessionCleaned());
            },
            (id, destination, namespaceName, _) =>
            {
                Assert.Equal("target-a", destination.DeploymentTargetId);
                Assert.Equal("cell-a", destination.KubernetesNamespace);
                deleted.Add((id, namespaceName));
                return id switch
                {
                    "conflict" => Task.FromResult<DeletePrivateAccessWorkerOutcome>(new PrivateAccessWorkerCleanupPending()),
                    "unreachable" => Task.FromException<DeletePrivateAccessWorkerOutcome>(new IOException("unreachable")),
                    _ => Task.FromResult<DeletePrivateAccessWorkerOutcome>(new PrivateAccessWorkerDeleted())
                };
            },
            TimeProvider.System,
            NullLogger<PrivateAccessSessionCleanup>.Instance);

        await cleanup.RunAsync(CancellationToken.None);

        Assert.Equal(["never-provisioned", "removed", "after-failure"], acknowledged);
        Assert.Equal(
            [("removed", "old-worker-namespace"), ("conflict", "old-worker-namespace"),
                ("unreachable", "another-worker-namespace"), ("after-failure", "another-worker-namespace")],
            deleted);
    }

    private static PrivateAccessSessionCleanupSnapshot Snapshot(string id, string? workerNamespace) => new(
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
        NetworkPort.Create(5432),
        workerNamespace);
}
