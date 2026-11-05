using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Application.RuntimeCells;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class RuntimeCellVerificationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task ReadyCellsWithoutWorkloads_AreRecheckedInBoundedBatches_WithAtomicOutbox()
    {
        var now = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        var oldest = CreateReady(now.AddMinutes(-20));
        var next = CreateReady(now.AddMinutes(-10));
        var recent = CreateReady(now.AddMinutes(-1));
        await DbContext.RuntimeCells.InsertManyAsync([oldest, next, recent]);
        var persistence = new RuntimeCellRecoveryPersistence(
            DbContext.IsolationScopes,
            DbContext.RuntimeCells,
            Fixture.StoreOutboxWrites,
            (operation, cancellationToken) => DbContext.RunOutcomeInTransactionAsync(
                operation, cancellationToken: cancellationToken));

        var candidates = await persistence.LoadRecoveryCandidatesAsync(now.AddSeconds(-30), now, 1);
        Assert.Equal(oldest.Id, Assert.Single(candidates).Id);
        await persistence.RecoverAsync(oldest.Id, now);
        await persistence.RecoverAsync(oldest.Id, now);

        var saved = await DbContext.RuntimeCells.Find(cell => cell.Id == oldest.Id).SingleAsync();
        Assert.IsType<ProvisioningRuntimeCellState>(saved.State);
        Assert.Equal(oldest.DesiredGeneration + 1, saved.DesiredGeneration);
        Assert.Equal(oldest.KubernetesNamespace, saved.KubernetesNamespace);
        Assert.Equal(1, await DbContext.OutboxMessages.CountDocumentsAsync(message =>
            message.PendingKey == RuntimeCellPendingKey.Reconciliation(saved.Id, saved.DesiredGeneration)));
        var remaining = await persistence.LoadRecoveryCandidatesAsync(now.AddSeconds(-30), now, 1);
        Assert.Equal(next.Id, Assert.Single(remaining).Id);
        await persistence.RecoverAsync(next.Id, now);
        Assert.Empty(await persistence.LoadRecoveryCandidatesAsync(now.AddSeconds(-30), now, 1));
    }

    private static RuntimeCell CreateReady(DateTime readyAtUtc) =>
        RuntimeCellFactory.Create(
                ObjectId.GenerateNewId().ToString(),
                ObjectId.GenerateNewId().ToString(),
                ObjectId.GenerateNewId().ToString(),
                ObjectId.GenerateNewId().ToString(),
                "pole-verification-test",
                new RuntimeCellFactory.CreateOptions(
                    ObjectId.GenerateNewId().ToString(), readyAtUtc.AddMinutes(-1)))
            .MarkReady(1, readyAtUtc);
}
