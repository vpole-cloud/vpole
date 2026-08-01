using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.ResourceDeployments.Application.Mutations;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.ResourceGroups.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Name)]
public sealed class ResourceDeploymentMutationWriterTests(
    MongoReplicaSetFixture fixture)
    : IAsyncLifetime
{
    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string OtherTenantId = "507f1f77bcf86cd799439012";
    private const string ResourceId = "507f1f77bcf86cd799439013";
    private const string ResourceGroupId = "507f1f77bcf86cd799439014";
    private const string ScopeId = "507f1f77bcf86cd799439015";
    private static readonly ResourceTypeId TestResourceType =
        ResourceTypeId.Create("pole/test-resource");
    private const string RuntimeCellId = "507f1f77bcf86cd799439016";
    private const string TargetId = "507f1f77bcf86cd799439017";
    private const string DeploymentId = "507f1f77bcf86cd799439018";
    private static readonly DateTime CreatedAtUtc =
        new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MutationAtUtc =
        CreatedAtUtc.AddMinutes(2);

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RequestApply_AdvancesGeneration_PreservesPlacement_AndQueuesCurrentRequest()
    {
        var (runtimeCell, deployment) = CreateAggregate(runtimeCellReady: true);
        await InsertAsync(runtimeCell, deployment);
        var writer = CreateWriter();

        var result = await fixture.DbContext
            .RunInTransactionAsync<ResourceDeployment>(
                (session, cancellationToken) => writer.RequestApplyAsync(
                    CreateCommand(),
                    session,
                    cancellationToken));

        var updated = result.AssertSuccess();
        Assert.Equal(2, updated.DesiredGeneration);
        Assert.Equal(deployment.ResolvedPlacement, updated.ResolvedPlacement);
        var pending = Assert.IsType<PendingResourceDeploymentState>(updated.State);
        Assert.Equal(ResourceDeploymentOperation.Apply, pending.Operation);
        var request = await fixture.DbContext.OutboxMessages
            .Find(message =>
                message.PendingKey == ResourceDeploymentPendingKey.Apply(
                    DeploymentId,
                    2))
            .SingleAsync();
        Assert.Equal("resource-deployment-reconciliation-requested", request.EventType);
    }

    [Fact]
    public async Task RequestApply_WaitsWithoutOutbox_WhenRuntimeCellIsNotReady()
    {
        var (runtimeCell, deployment) = CreateAggregate(runtimeCellReady: false);
        await InsertAsync(runtimeCell, deployment);
        var writer = CreateWriter();

        var result = await fixture.DbContext
            .RunInTransactionAsync<ResourceDeployment>(
                (session, cancellationToken) => writer.RequestApplyAsync(
                    CreateCommand(),
                    session,
                    cancellationToken));

        var updated = result.AssertSuccess();
        var waiting = Assert.IsType<WaitingForRuntimeCellResourceDeploymentState>(
            updated.State);
        Assert.Equal(2, waiting.Generation);
        Assert.Equal(
            0,
            await fixture.DbContext.OutboxMessages.CountDocumentsAsync(
                FilterDefinition<Pole.Features.Shared.Models.OutboxMessage>.Empty));
    }

    [Fact]
    public async Task RequestDeletion_AdvancesGeneration_AndQueuesDeleteRequest()
    {
        var (runtimeCell, deployment) = CreateAggregate(runtimeCellReady: true);
        await InsertAsync(runtimeCell, deployment);
        var writer = CreateWriter();

        var result = await fixture.DbContext
            .RunInTransactionAsync<ResourceDeployment>(
                (session, cancellationToken) => writer.RequestDeletionAsync(
                    CreateCommand(),
                    session,
                    cancellationToken));

        var updated = result.AssertSuccess();
        Assert.Equal(2, updated.DesiredGeneration);
        Assert.Equal(ResourceDeploymentOperation.Delete, updated.DesiredOperation);
        var pending = Assert.IsType<PendingResourceDeploymentState>(updated.State);
        Assert.Equal(ResourceDeploymentOperation.Delete, pending.Operation);
        var request = await fixture.DbContext.OutboxMessages
            .Find(message =>
                message.PendingKey == ResourceDeploymentPendingKey.Delete(
                    DeploymentId,
                    2))
            .SingleAsync();
        Assert.Equal("resource-deployment-deletion-requested", request.EventType);
    }

    [Fact]
    public async Task Mutation_IsTenantScoped()
    {
        var (runtimeCell, deployment) = CreateAggregate(runtimeCellReady: true);
        await InsertAsync(runtimeCell, deployment);
        var writer = CreateWriter();

        var result = await fixture.DbContext
            .RunInTransactionAsync<ResourceDeployment>(
                (session, cancellationToken) => writer.RequestApplyAsync(
                    CreateCommand() with { TenantId = OtherTenantId },
                    session,
                    cancellationToken));

        Assert.IsType<NotFoundFailure>(result.AssertFailure());
        var saved = await fixture.DbContext.ResourceDeployments
            .Find(item => item.Id == DeploymentId)
            .SingleAsync();
        Assert.Equal(1, saved.DesiredGeneration);
    }

    [Fact]
    public async Task CallerFailure_AbortsDeploymentAndOutboxWrites()
    {
        var (runtimeCell, deployment) = CreateAggregate(runtimeCellReady: true);
        await InsertAsync(runtimeCell, deployment);
        var writer = CreateWriter();

        var result = await fixture.DbContext
            .RunInTransactionAsync<ResourceDeployment>(
                async (session, cancellationToken) =>
                {
                    var mutation = await writer.RequestApplyAsync(
                        CreateCommand(),
                        session,
                        cancellationToken);
                    _ = mutation.AssertSuccess();
                    return Failures.Conflict("The logical resource write failed.");
                });

        Assert.IsType<ConflictFailure>(result.AssertFailure());
        var saved = await fixture.DbContext.ResourceDeployments
            .Find(item => item.Id == DeploymentId)
            .SingleAsync();
        Assert.Equal(1, saved.DesiredGeneration);
        Assert.Equal(
            0,
            await fixture.DbContext.OutboxMessages.CountDocumentsAsync(
                FilterDefinition<Pole.Features.Shared.Models.OutboxMessage>.Empty));
    }

    [Fact]
    public async Task Mutation_RequiresCallerOwnedTransaction()
    {
        var writer = CreateWriter();
        using var session = await fixture.DbContext.Client.StartSessionAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.RequestApplyAsync(CreateCommand(), session));

        Assert.Contains(
            "logical resource transaction",
            exception.Message,
            StringComparison.Ordinal);
    }

    private ResourceDeploymentMutationWriter CreateWriter() =>
        new(
            fixture.DbContext,
            new FixedTimeProvider(new DateTimeOffset(
                MutationAtUtc,
                TimeSpan.Zero)));

    private static ResourceDeploymentMutationCommand CreateCommand() =>
        new(
            TenantId,
            TestResourceType,
            ResourceId);

    private async Task InsertAsync(
        RuntimeCell runtimeCell,
        ResourceDeployment deployment)
    {
        await fixture.DbContext.RuntimeCells.InsertOneAsync(runtimeCell);
        await fixture.DbContext.ResourceDeployments.InsertOneAsync(deployment);
    }

    private static (RuntimeCell RuntimeCell, ResourceDeployment Deployment)
        CreateAggregate(bool runtimeCellReady)
    {
        var runtimeCell = RuntimeCellFactory.Create(
            TenantId,
            ResourceGroupId,
            ScopeId,
            TargetId,
            "pole-test-scope",
            new RuntimeCellFactory.CreateOptions(
                RuntimeCellId,
                CreatedAtUtc));
        if (runtimeCellReady)
        {
            runtimeCell = runtimeCell.MarkReady(
                expectedGeneration: 1,
                CreatedAtUtc.AddMinutes(1));
        }

        var placement = ResolvedResourcePlacement.Create(
            RuntimeCellId,
            TargetId,
            runtimeCell.KubernetesNamespace,
            runtimeCellGeneration: 1,
            targetWorkloadConfigurationRevision: 1,
            ResolvedIngressConfiguration.Create(
                "traefik",
                "letsencrypt-prod"),
            storage: null,
            CreatedAtUtc);
        var deployment = ResourceDeploymentFactory.Create(
            TenantId,
            TestResourceType,
            ResourceId,
            ResourceGroupId,
            ScopeId,
            placement,
            ResourcePlacementCapability.SingleTarget,
            runtimeCellReady,
            new ResourceDeploymentFactory.CreateOptions(
                DeploymentId,
                CreatedAtUtc));
        return (runtimeCell, deployment);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
