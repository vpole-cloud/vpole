using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Application.Realtime;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.WebServices.Application.Realtime;
using Pole.Observability;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResourceDeploymentRealtimePersistenceTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    private const string StartedEventName = "pole.resource-deployments/v1/resource-deployment-reconciliation-started";

    [Theory]
    [InlineData(ResourceDeploymentOperation.Apply)]
    [InlineData(ResourceDeploymentOperation.Delete)]
    public async Task Claim_CommitsStartedSnapshot_AndFencesTenantGenerationAndConcurrentClaims(
        ResourceDeploymentOperation operation)
    {
        var deployment = Deployment(operation);
        await DbContext.ResourceDeployments.InsertOneAsync(deployment);
        var claim = Services.GetRequiredService<ClaimResourceDeployment>();
        Assert.Null(await claim(ObjectId.GenerateNewId().ToString(),
            deployment.Id,
            deployment.RuntimeCellId,
            deployment.DesiredGeneration,
            operation));
        Assert.Null(await claim(deployment.TenantId,
            deployment.Id,
            deployment.RuntimeCellId,
            deployment.DesiredGeneration + 1,
            operation));
        using var trace = TraceContext.Push(TraceContext.Create("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "claim-pilot"));
        var results = await Task.WhenAll(
            claim(deployment.TenantId,
                deployment.Id,
                deployment.RuntimeCellId,
                deployment.DesiredGeneration,
                operation),
            claim(deployment.TenantId,
                deployment.Id,
                deployment.RuntimeCellId,
                deployment.DesiredGeneration,
                operation));
        var claimed = Assert.Single(results, result => result is not null)!;
        Assert.IsType<ReconcilingResourceDeploymentState>(claimed.State);
        var envelope = await DbContext.OutboxMessages.Find(x => x.EventType == StartedEventName).SingleAsync();
        Assert.Equal("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", envelope.TraceId);
        Assert.Equal("claim-pilot", envelope.CorrelationId);
        var message =
            JsonSerializer.Deserialize<ResourceDeploymentReconciliationStartedEvent>(envelope.PayloadJson,
                EventExtensions.SerializerOptions)!;
        Assert.Equal(deployment.TenantId, message.EventCore.TenantId);
        Assert.Equal(deployment.ResourceId, message.ResourceId);
        Assert.Equal(deployment.ResourceGroupId, message.ResourceGroupId);
        Assert.Equal(deployment.DesiredGeneration, message.Generation);
        Assert.Equal(operation.ToString(), message.Operation);
    }

    [Fact]
    public async Task Claim_OutboxFailureRollsBackStartedState()
    {
        var deployment = Deployment(ResourceDeploymentOperation.Apply);
        await DbContext.ResourceDeployments.InsertOneAsync(deployment);
        var claim = new ClaimResourceDeploymentOperation(DbContext.ResourceDeployments,
            new ResourceDeploymentSettings(),
            TimeProvider.System,
            new MongoRunTransaction(Fixture.Client),
            async (session, writes, ct) =>
            {
                await Fixture.StoreOutboxWrites(session, writes, ct);
                throw new InvalidOperationException("Injected outbox failure.");
            });
        await Assert.ThrowsAsync<InvalidOperationException>(() => claim.InvokeAsync(deployment.TenantId,
            deployment.Id,
            deployment.RuntimeCellId,
            deployment.DesiredGeneration,
            ResourceDeploymentOperation.Apply));
        var stored = await DbContext.ResourceDeployments.Find(x => x.Id == deployment.Id).SingleAsync();
        Assert.IsType<PendingResourceDeploymentState>(stored.State);
        Assert.Equal(deployment.Revision, stored.Revision);
        Assert.Equal(0, await DbContext.OutboxMessages.CountDocumentsAsync(x => x.EventType == StartedEventName));
    }

    [Fact]
    public async Task DeleteCompletion_NotifiesLogicalResourceAndGroupEvenAfterDeploymentPurge()
    {
        var deployment = Deployment(ResourceDeploymentOperation.Delete);
        await DbContext.ResourceDeployments.InsertOneAsync(deployment);
        var claimed = await Services.GetRequiredService<ClaimResourceDeployment>()(deployment.TenantId,
            deployment.Id,
            deployment.RuntimeCellId,
            deployment.DesiredGeneration,
            ResourceDeploymentOperation.Delete);
        await Services.GetRequiredService<PersistResourceDeploymentSuccess>()(claimed!,
            deployment.DesiredGeneration,
            ResourceDeploymentOperation.Delete,
            []);
        var envelope = await DbContext.OutboxMessages
            .Find(x => x.EventType == "pole.resource-deployments/v1/resource-deployment-deleted").SingleAsync();
        var message =
            JsonSerializer.Deserialize<ResourceDeploymentDeletedEvent>(envelope.PayloadJson,
                EventExtensions.SerializerOptions)!;
        await DbContext.ResourceDeployments.DeleteOneAsync(x => x.Id == deployment.Id);
        var notices = new List<RealtimeChange>();

        Task Publish(RealtimeChange change, CancellationToken _)
        {
            notices.Add(change);
            return Task.CompletedTask;
        }

        var operations = new NotifyResourceDeploymentChangedConsumer(Publish);
        var webServices = new NotifyWebServiceChangedConsumer(Publish,
            (_, _, _) => throw new InvalidOperationException("Deletion must use the event snapshot."));
        await operations.HandleAsync(message);
        await webServices.HandleAsync(message);
        await operations.HandleAsync(message);
        Assert.Equal(["operations", "web-services", "operations"], notices.Select(notice => notice.Topic));
        Assert.All(notices,
            notice =>
            {
                Assert.Equal(deployment.TenantId, notice.TenantId);
                Assert.Equal(deployment.ResourceId, notice.ResourceId);
                Assert.Equal(deployment.ResourceGroupId, notice.ResourceGroupId);
                Assert.Equal(deployment.DesiredGeneration, notice.Generation);
            });
    }

    private static ResourceDeployment Deployment(ResourceDeploymentOperation operation)
    {
        var now = DateTime.UtcNow.AddMinutes(-1);
        var deployment = ResourceDeploymentFactory.Create(TestData.TenantId,
            ResourceTypeId.Create("pole/web-service"),
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            ResolvedResourcePlacement.Create(ObjectId.GenerateNewId().ToString(),
                ObjectId.GenerateNewId().ToString(),
                "pole-realtime-test",
                1,
                1,
                null,
                null,
                ResourceDeploymentTestData.Compute(),
                now),
            ResourcePlacementCapability.SingleTarget,
            true,
            ResourceDeploymentFactory.DefaultOptions with
            {
                Id = ObjectId.GenerateNewId().ToString(),
                CreatedAtUtc = now
            });
        return operation == ResourceDeploymentOperation.Delete
            ? deployment.RequestDeletion(now.AddSeconds(1))
            : deployment;
    }
}
