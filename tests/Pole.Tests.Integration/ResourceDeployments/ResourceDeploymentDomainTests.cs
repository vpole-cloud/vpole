using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResourceDeploymentDomainTests
{
    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string ResourceId = "507f1f77bcf86cd799439012";
    private const string ResourceGroupId = "507f1f77bcf86cd799439013";
    private const string ScopeId = "507f1f77bcf86cd799439014";
    private const string RuntimeCellId = "507f1f77bcf86cd799439015";
    private const string TargetId = "507f1f77bcf86cd799439016";
    private const string DeploymentId = "507f1f77bcf86cd799439017";
    private const string ClaimId = "507f1f77bcf86cd799439018";
    private static readonly ResourceTypeId TestResourceType =
        ResourceTypeId.Create("pole/test-resource");
    private static readonly DateTime CreatedAtUtc =
        new(2026, 7, 29, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Factory_CreatesSingleTargetPlacementKey_AndWaitingState()
    {
        var deployment = CreateDeployment(runtimeCellReady: false);

        Assert.Equal(
            $"{TestResourceType.Value}:{ResourceId}:single",
            deployment.PlacementKey);
        Assert.Equal(ResourcePlacementCapability.SingleTarget, deployment.PlacementCapability);
        Assert.Equal(1, deployment.DesiredGeneration);
        Assert.Equal(0, deployment.ObservedGeneration);
        Assert.IsType<WaitingForRuntimeCellResourceDeploymentState>(deployment.State);
        Assert.Empty(deployment.Endpoints);

        var document = deployment.ToBsonDocument();
        Assert.Equal(TestResourceType.Value, document["ResourceType"].AsString);
        Assert.Equal(0, document["PlacementCapability"].AsInt32);
    }

    [Fact]
    public void NewIntentDuringClaim_IsQueuedAfterCurrentOperationCompletes()
    {
        var claimed = CreateDeployment(runtimeCellReady: true).Start(
            expectedGeneration: 1,
            ResourceDeploymentOperation.Apply,
            ClaimId,
            CreatedAtUtc.AddMinutes(1),
            CreatedAtUtc.AddMinutes(6));
        var changed = claimed.RequestReconciliation(
            claimed.ResolvedPlacement,
            runtimeCellReady: true,
            CreatedAtUtc.AddMinutes(2));

        Assert.Equal(2, changed.DesiredGeneration);
        Assert.IsType<ReconcilingResourceDeploymentState>(changed.State);

        var completed = changed.Complete(
            expectedGeneration: 1,
            ResourceDeploymentOperation.Apply,
            ClaimId,
            [ResourceDeploymentEndpoint.Create("Public", "app.example.test", 443, "https")],
            CreatedAtUtc.AddMinutes(3));

        var pending = Assert.IsType<PendingResourceDeploymentState>(completed.State);
        Assert.Equal(2, pending.Generation);
        Assert.Equal(ResourceDeploymentOperation.Apply, pending.Operation);
        Assert.Equal(1, completed.ObservedGeneration);
    }

    [Fact]
    public void DeleteDuringApplyClaim_IsSerializedBehindCurrentClaim()
    {
        var claimed = CreateDeployment(runtimeCellReady: true).Start(
            expectedGeneration: 1,
            ResourceDeploymentOperation.Apply,
            ClaimId,
            CreatedAtUtc.AddMinutes(1),
            CreatedAtUtc.AddMinutes(6));

        var deleting = claimed.RequestDeletion(CreatedAtUtc.AddMinutes(2));
        var afterApply = deleting.Complete(
            expectedGeneration: 1,
            ResourceDeploymentOperation.Apply,
            ClaimId,
            [],
            CreatedAtUtc.AddMinutes(3));

        var pendingDelete = Assert.IsType<PendingResourceDeploymentState>(afterApply.State);
        Assert.Equal(ResourceDeploymentOperation.Delete, pendingDelete.Operation);
        Assert.Equal(2, pendingDelete.Generation);
    }

    [Fact]
    public void FailedCurrentOperation_CanBeRetriedWithoutChangingGeneration()
    {
        var claimed = CreateDeployment(runtimeCellReady: true).Start(
            expectedGeneration: 1,
            ResourceDeploymentOperation.Apply,
            ClaimId,
            CreatedAtUtc.AddMinutes(1),
            CreatedAtUtc.AddMinutes(6));
        var failed = claimed.Fail(
            expectedGeneration: 1,
            ResourceDeploymentOperation.Apply,
            ClaimId,
            "KubernetesUnavailable",
            "Kubernetes API is temporarily unavailable.",
            retryable: true,
            CreatedAtUtc.AddMinutes(2),
            CreatedAtUtc.AddMinutes(3));

        var retried = failed.Retry(CreatedAtUtc.AddMinutes(3));

        var pending = Assert.IsType<PendingResourceDeploymentState>(retried.State);
        Assert.Equal(1, pending.Generation);
        Assert.Equal(ResourceDeploymentOperation.Apply, pending.Operation);
    }

    [Fact]
    public void AppliedDeployment_RoundTripsStateAndGenericEndpoints()
    {
        var claimed = CreateDeployment(runtimeCellReady: true).Start(
            expectedGeneration: 1,
            ResourceDeploymentOperation.Apply,
            ClaimId,
            CreatedAtUtc.AddMinutes(1),
            CreatedAtUtc.AddMinutes(6));
        var applied = claimed.Complete(
            expectedGeneration: 1,
            ResourceDeploymentOperation.Apply,
            ClaimId,
            [ResourceDeploymentEndpoint.Create("Public", "app.example.test", 443, "https")],
            CreatedAtUtc.AddMinutes(2));

        var roundTripped = BsonSerializer.Deserialize<ResourceDeployment>(
            applied.ToBson());

        Assert.IsType<AppliedResourceDeploymentState>(roundTripped.State);
        var endpoint = Assert.Single(roundTripped.Endpoints);
        Assert.Equal("Public", endpoint.Kind);
        Assert.Equal("app.example.test", endpoint.Host);

        var endpointDocument = applied.ToBsonDocument()["Endpoints"]
            .AsBsonArray[0]
            .AsBsonDocument;
        Assert.False(endpointDocument.Contains("_t"));
        Assert.Equal("Public", endpointDocument["Kind"].AsString);
        Assert.Equal("https", endpointDocument["Scheme"].AsString);
        Assert.Equal(TargetId, roundTripped.ResolvedPlacement.DeploymentTargetId);
        Assert.Equal(
            ResourceDeploymentTestData.ComputeProfileId,
            roundTripped.ResolvedPlacement.Compute.ComputeProfileId);
        Assert.Equal(250, roundTripped.ResolvedPlacement.Compute.Requests.CpuMillis);
    }

    private static ResourceDeployment CreateDeployment(bool runtimeCellReady)
    {
        var placement = ResolvedResourcePlacement.Create(
            RuntimeCellId,
            TargetId,
            "pole-t-tenant-is-scope",
            runtimeCellGeneration: 1,
            targetWorkloadConfigurationRevision: 1,
            ResolvedIngressConfiguration.Create("traefik", "letsencrypt-prod"),
            storage: null,
            ResourceDeploymentTestData.Compute(),
            CreatedAtUtc);

        return ResourceDeploymentFactory.Create(
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
    }
}
