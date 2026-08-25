using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Persistence;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Name)]
public sealed class ResourceDeploymentFinalizationCapabilityTests(
    MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    private const string TenantId = "507f1f77bcf86cd799439081";
    private const string OtherTenantId = "507f1f77bcf86cd799439082";
    private const string ResourceId = "507f1f77bcf86cd799439083";
    private const string ResourceGroupId = "507f1f77bcf86cd799439084";
    private const string IsolationScopeId = "507f1f77bcf86cd799439085";
    private const string RuntimeCellId = "507f1f77bcf86cd799439086";
    private const string TargetId = "507f1f77bcf86cd799439087";
    private static readonly ResourceTypeId ResourceType =
        ResourceTypeId.Create("pole/finalization-test");
    private static readonly DateTime CreatedAtUtc =
        new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Finalization_WaitsUntilEveryDeploymentIsDeleted()
    {
        await fixture.DbContext.ResourceDeployments.InsertManyAsync(
        [
            CreateDeployment(
                "507f1f77bcf86cd799439088",
                TenantId,
                deleted: true),
            CreateDeployment(
                "507f1f77bcf86cd799439089",
                TenantId,
                deleted: false)
        ]);
        var operations = CreateOperations();

        var result = await RunTypedTransactionAsync(
            async (session, cancellationToken) =>
            {
                var readiness = await operations.GetReadiness(
                    TenantId,
                    ResourceType,
                    ResourceId,
                    session,
                    cancellationToken);
                Assert.Equal(ResourceDeploymentFinalizationReadiness.Waiting, readiness);
                return await operations.PurgeDeleted(
                    TenantId,
                    ResourceType,
                    ResourceId,
                    session,
                    cancellationToken);
            });

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal(ResourceId, error.ResourceId);
        Assert.Equal(
            2,
            await fixture.DbContext.ResourceDeployments.CountDocumentsAsync(
                deployment => deployment.TenantId == TenantId));
    }

    [Fact]
    public async Task Finalization_PurgesOnlyTheRequestedTenantInsideCallerTransaction()
    {
        await fixture.DbContext.ResourceDeployments.InsertManyAsync(
        [
            CreateDeployment(
                "507f1f77bcf86cd79943908a",
                TenantId,
                deleted: true),
            CreateDeployment(
                "507f1f77bcf86cd79943908b",
                OtherTenantId,
                deleted: true)
        ]);
        var operations = CreateOperations();

        var result = await RunTypedTransactionAsync(
            async (session, cancellationToken) =>
            {
                var readiness = await operations.GetReadiness(
                    TenantId,
                    ResourceType,
                    ResourceId,
                    session,
                    cancellationToken);
                Assert.Equal(ResourceDeploymentFinalizationReadiness.Ready, readiness);
                return await operations.PurgeDeleted(
                    TenantId,
                    ResourceType,
                    ResourceId,
                    session,
                    cancellationToken);
            });

        Assert.True(result.TryGetValue(out _, out _));
        Assert.Equal(
            0,
            await fixture.DbContext.ResourceDeployments.CountDocumentsAsync(
                deployment => deployment.TenantId == TenantId));
        Assert.Equal(
            1,
            await fixture.DbContext.ResourceDeployments.CountDocumentsAsync(
                deployment => deployment.TenantId == OtherTenantId));
    }

    [Fact]
    public async Task ExactFinalization_PurgesOnlyTheRequestedDeletedDeployment()
    {
        const string deletedDeploymentId = "507f1f77bcf86cd79943908d";
        const string siblingDeploymentId = "507f1f77bcf86cd79943908e";
        await fixture.DbContext.ResourceDeployments.InsertManyAsync(
        [
            CreateDeployment(deletedDeploymentId, TenantId, deleted: true),
            CreateDeployment(siblingDeploymentId, TenantId, deleted: false)
        ]);
        var operations = CreateOperations();

        var result = await RunTypedTransactionAsync(
            async (session, cancellationToken) =>
            {
                var readiness = await operations.GetExactReadiness(
                    TenantId,
                    ResourceType,
                    ResourceId,
                    deletedDeploymentId,
                    session,
                    cancellationToken);
                Assert.Equal(ResourceDeploymentFinalizationReadiness.Ready, readiness);
                return await operations.PurgeExactDeleted(
                    TenantId,
                    ResourceType,
                    ResourceId,
                    deletedDeploymentId,
                    session,
                    cancellationToken);
            });

        Assert.True(result.TryGetValue(out _, out _));
        Assert.Equal(
            1,
            await fixture.DbContext.ResourceDeployments.CountDocumentsAsync(
                deployment => deployment.TenantId == TenantId));
        var remaining = await fixture.DbContext.ResourceDeployments
            .Find(deployment => deployment.Id == siblingDeploymentId)
            .SingleAsync();
        Assert.IsNotType<DeletedResourceDeploymentState>(remaining.State);
    }

    private async Task<Result<Unit, ResourceDeploymentFinalizationConflict>>
        RunTypedTransactionAsync(
            Func<
                IClientSessionHandle,
                CancellationToken,
                Task<Result<Unit, ResourceDeploymentFinalizationConflict>>> operation)
    {
        using var session = await fixture.DbContext.Client.StartSessionAsync();
        return await session.WithTransactionAsync(operation);
    }

    private FinalizationOperations CreateOperations() => new(
        new MongoGetResourceDeploymentFinalizationReadiness(
            fixture.DbContext.ResourceDeployments).InvokeAsync,
        new MongoPurgeDeletedResourceDeployments(
            fixture.DbContext.ResourceDeployments).InvokeAsync,
        new MongoGetExactResourceDeploymentFinalizationReadiness(
            fixture.DbContext.ResourceDeployments).InvokeAsync,
        new MongoPurgeExactDeletedResourceDeployment(
            fixture.DbContext.ResourceDeployments).InvokeAsync);

    private sealed record FinalizationOperations(
        GetResourceDeploymentFinalizationReadiness GetReadiness,
        PurgeDeletedResourceDeployments PurgeDeleted,
        GetExactResourceDeploymentFinalizationReadiness GetExactReadiness,
        PurgeExactDeletedResourceDeployment PurgeExactDeleted);

    private static ResourceDeployment CreateDeployment(
        string deploymentId,
        string tenantId,
        bool deleted)
    {
        var deployment = ResourceDeploymentFactory.Create(
            tenantId,
            ResourceType,
            ResourceId,
            ResourceGroupId,
            IsolationScopeId,
            ResolvedResourcePlacement.Create(
                RuntimeCellId,
                TargetId,
                "pole-finalization-test",
                runtimeCellGeneration: 1,
                targetWorkloadConfigurationRevision: 1,
                ingress: null,
                storage: null,
                ResourceDeploymentTestData.Compute(),
                CreatedAtUtc),
            ResourcePlacementCapability.SingleTarget,
            runtimeCellReady: true,
            new ResourceDeploymentFactory.CreateOptions(
                deploymentId,
                CreatedAtUtc));
        if (!deleted)
            return deployment;

        const string claimId = "507f1f77bcf86cd79943908c";
        var requested = deployment.RequestDeletion(CreatedAtUtc.AddSeconds(1));
        var started = requested.Start(
            expectedGeneration: 2,
            ResourceDeploymentOperation.Delete,
            claimId,
            CreatedAtUtc.AddSeconds(2),
            CreatedAtUtc.AddMinutes(1));
        return started.Complete(
            expectedGeneration: 2,
            ResourceDeploymentOperation.Delete,
            claimId,
            [],
            CreatedAtUtc.AddSeconds(3));
    }
}
