using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Endpoints;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Events;
using Pole.Mediator;
using Pole.Tests.Integration.Infrastructure;
using Xunit;
using Xunit.Sdk;

namespace Pole.Tests.Integration.ResourceDeployments;

[Collection(IntegrationTestCollection.Tenant3)]
public sealed class RetryResourceDeploymentTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    private static readonly ResourceTypeId TestResourceType = ResourceTypeId.Create("pole/test-retry");

    private static readonly DateTime CreatedAtUtc = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly string ClaimId = ObjectId.GenerateNewId().ToString();
    private readonly string DeploymentId = ObjectId.GenerateNewId().ToString();
    private readonly string OtherTenantId = ObjectId.GenerateNewId().ToString();
    private readonly string ResourceGroupId = ObjectId.GenerateNewId().ToString();
    private readonly string ResourceId = ObjectId.GenerateNewId().ToString();
    private readonly string RuntimeCellId = ObjectId.GenerateNewId().ToString();
    private readonly string ScopeId = ObjectId.GenerateNewId().ToString();
    private readonly string TargetId = ObjectId.GenerateNewId().ToString();
    private string TenantId => Data.TenantId;

    private FilterDefinition<OutboxMessage> CaseOutbox =>
        Builders<OutboxMessage>.Filter.Where(message =>
            message.TenantId == TenantId || message.TenantId == OtherTenantId);

    [Fact]
    public async Task Retry_IsTenantScopedAndAbortsWithoutOutbox()
    {
        var deployment = CreateFailedDeployment(retryable: true);
        await DbContext.ResourceDeployments.InsertOneAsync(deployment);

        var result = await Mediator.Send(
            new RetryResourceDeploymentRequest(
                DeploymentId,
                OtherTenantId,
                deployment.Revision,
                deployment.DesiredOperation.ToString()));

        var notFound = AssertRetryError<ResourceDeploymentNotFound>(result);
        Assert.Equal(DeploymentId, notFound.DeploymentId);
        var persisted = await LoadDeploymentAsync();
        Assert.Equal(deployment.Revision, persisted.Revision);
        Assert.IsType<FailedResourceDeploymentState>(persisted.State);
        Assert.Equal(0, await DbContext.OutboxMessages.CountDocumentsAsync(CaseOutbox));
    }

    [Fact]
    public async Task Retry_NonRetryableFailureAbortsWithoutMutationOrOutbox()
    {
        var deployment = CreateFailedDeployment(retryable: false);
        await DbContext.ResourceDeployments.InsertOneAsync(deployment);

        var result = await Mediator.Send(
            new RetryResourceDeploymentRequest(
                DeploymentId,
                TenantId,
                deployment.Revision,
                deployment.DesiredOperation.ToString()));

        var notRetryable = AssertRetryError<ResourceDeploymentNotRetryable>(result);
        Assert.Equal(DeploymentId, notRetryable.DeploymentId);
        var persisted = await LoadDeploymentAsync();
        Assert.Equal(deployment.Revision, persisted.Revision);
        Assert.IsType<FailedResourceDeploymentState>(persisted.State);
        Assert.Equal(0, await DbContext.OutboxMessages.CountDocumentsAsync(CaseOutbox));
    }

    [Fact]
    public async Task Retry_CommitsMutationAndTenantOutboxTogether()
    {
        var deployment = CreateFailedDeployment(retryable: true);
        await DbContext.ResourceDeployments.InsertOneAsync(deployment);

        var result = await Mediator.Send(
            new RetryResourceDeploymentRequest(
                DeploymentId,
                TenantId,
                deployment.Revision,
                deployment.DesiredOperation.ToString()));

        var retried = AssertRetrySuccess(result);
        Assert.Equal(DeploymentId, retried.Id);
        Assert.Equal(deployment.Revision + 1, retried.Placement.DeploymentRevision);
        var persisted = await LoadDeploymentAsync();
        var pending = Assert.IsType<PendingResourceDeploymentState>(persisted.State);
        Assert.Equal(ResourceDeploymentOperation.Apply, pending.Operation);
        Assert.Equal(deployment.Revision + 1, persisted.Revision);
        var message = await DbContext.OutboxMessages.Find(CaseOutbox).SingleAsync();
        Assert.Equal("pole.resource-deployments/v1/resource-deployment-reconciliation-requested", message.EventType);
        Assert.Equal(TenantId, message.TenantId);
        Assert.Equal(
            ResourceDeploymentPendingKey.Apply(DeploymentId, deployment.DesiredGeneration),
            message.PendingKey);
    }

    [Fact]
    public async Task RetryCapability_RequiresCallerOwnedTransaction()
    {
        var retry = Services.GetRequiredService<RetryResourceDeployment>();
        using var session = await DbContext.Client.StartSessionAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => retry(
            TenantId,
            DeploymentId,
            1,
            ResourceDeploymentOperation.Apply,
            session));

        Assert.Contains("logical resource transaction", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retry_rejects_stale_revision_or_changed_operation_without_outbox(bool staleRevision)
    {
        var deployment = CreateFailedDeployment(retryable: true);
        await DbContext.ResourceDeployments.InsertOneAsync(deployment);
        var result = await Mediator.Send(
            new RetryResourceDeploymentRequest(
                DeploymentId,
                TenantId,
                staleRevision ? deployment.Revision + 1 : deployment.Revision,
                staleRevision ? "Apply" : "Delete"));
        AssertRetryError<ResourceDeploymentRetryConflict>(result);
        Assert.Equal(deployment.Revision, (await LoadDeploymentAsync()).Revision);
        Assert.IsType<FailedResourceDeploymentState>((await LoadDeploymentAsync()).State);
        Assert.Equal(0, await DbContext.OutboxMessages.CountDocumentsAsync(CaseOutbox));
    }

    private Task<ResourceDeployment> LoadDeploymentAsync() => DbContext.ResourceDeployments
        .Find(deployment => deployment.Id == DeploymentId)
        .SingleAsync();

    private ResourceDeployment CreateFailedDeployment(bool retryable)
    {
        var placement = ResolvedResourcePlacement.Create(
            RuntimeCellId,
            TargetId,
            "pole-t-retry-is-scope",
            runtimeCellGeneration: 1,
            targetWorkloadConfigurationRevision: 1,
            ResolvedIngressConfiguration.Create("traefik", "letsencrypt-prod"),
            storage: null,
            ResourceDeploymentTestData.Compute(),
            CreatedAtUtc);
        var deployment = ResourceDeploymentFactory.Create(
            TenantId,
            TestResourceType,
            ResourceId,
            ResourceGroupId,
            ScopeId,
            placement,
            ResourcePlacementCapability.SingleTarget,
            runtimeCellReady: true,
            ResourceDeploymentFactory.DefaultOptions with
            {
                Id = DeploymentId,
                CreatedAtUtc = CreatedAtUtc,
            });
        var claimed = deployment.Start(
            expectedGeneration: 1,
            ResourceDeploymentOperation.Apply,
            ClaimId,
            CreatedAtUtc.AddMinutes(1),
            CreatedAtUtc.AddMinutes(5));
        return claimed.Fail(
            expectedGeneration: 1,
            ResourceDeploymentOperation.Apply,
            ClaimId,
            "ProviderUnavailable",
            "The provider was unavailable.",
            retryable,
            CreatedAtUtc.AddMinutes(2),
            CreatedAtUtc.AddMinutes(2));
    }

    private static TError AssertRetryError<TError>(MediatorResult<RetryResourceDeploymentOutcome> result)
        where TError : notnull
    {
        var outcome = HandlerOutcome(result);
        var error = outcome switch
        {
            ResourceDeploymentDto => throw new XunitException("Expected a retry error, but received a deployment."),
            RetryResourceDeploymentError retryError => retryError,
        };
        return Assert.IsType<TError>(error.Value);
    }

    private static ResourceDeploymentDto AssertRetrySuccess(MediatorResult<RetryResourceDeploymentOutcome> result) =>
        HandlerOutcome(result) switch
        {
            ResourceDeploymentDto deployment => deployment,
            RetryResourceDeploymentError error => throw new XunitException(
                $"Expected a deployment, but received " + $"'{error.Value.GetType().Name}'."),
        };

    private static RetryResourceDeploymentOutcome
        HandlerOutcome(MediatorResult<RetryResourceDeploymentOutcome> result) => result.Match(
        outcome => outcome,
        rejected => throw new XunitException(
            $"Expected a handler outcome, but the pipeline returned " + $"'{rejected.Reason.GetType().Name}'."));
}
