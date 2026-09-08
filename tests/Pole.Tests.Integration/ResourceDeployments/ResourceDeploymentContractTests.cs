using System.Reflection;
using System.Text.Json;
using MassTransit;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Reconciliation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Xunit;
using EventJson = Pole.Features.Events.Abstractions.EventExtensions;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class ResourceDeploymentContractTests
{
    private const string TenantId = "507f1f77bcf86cd799439011";
    private const string DeploymentId = "507f1f77bcf86cd799439012";
    private const string RuntimeCellId = "507f1f77bcf86cd799439013";
    private static readonly ResourceTypeId TestResourceType =
        ResourceTypeId.Create("pole/test-resource");

    [Fact]
    public void ResourceTypeId_ValidatesAndNormalizesStrings()
    {
        Assert.Equal("pole/test-resource", TestResourceType.Value);
        Assert.Equal("pole/test-resource", ResourceTypeId.Create(" pole/test-resource ").Value);
        Assert.True(ResourceTypeId.TryCreate(" pole/test-resource ", out var resourceType));
        Assert.Equal("pole/test-resource", resourceType.Value);
        Assert.False(ResourceTypeId.TryCreate("WebService", out _));
        Assert.False(ResourceTypeId.TryCreate(null, out _));
        Assert.Throws<ArgumentException>(() => ResourceTypeId.Create("WebService"));
        Assert.Throws<ArgumentException>(() => ResourceTypeId.Create("pole/"));
    }

    [Fact]
    public void AdapterCatalog_RejectsDuplicateResourceTypes()
    {
        var descriptor = ResourceDeploymentAdapterDescriptor.Create<FakeAdapter>(
            TestResourceType,
            ResourcePlacementCapability.SingleTarget);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new ResourceDeploymentAdapterCatalog([descriptor, descriptor]));

        Assert.Contains(
            TestResourceType.Value,
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsValidator_RejectsUnsafeBounds()
    {
        var result = new ResourceDeploymentSettingsValidator().Validate(
            null,
            new ResourceDeploymentSettings
            {
                RecoveryBatchSize = 0,
                ClaimLeaseSeconds = 1,
                InitialRetryDelaySeconds = 30,
                MaximumRetryDelaySeconds = 10
            });

        Assert.True(result.Failed);
        Assert.Equal(3, result.Failures.Count());
    }

    [Theory]
    [InlineData(
        typeof(ResourceDeploymentReconciliationRequestedEvent),
        "pole.resource-deployments/v1/resource-deployment-reconciliation-requested")]
    [InlineData(
        typeof(ResourceDeploymentDeletionRequestedEvent),
        "pole.resource-deployments/v1/resource-deployment-deletion-requested")]
    [InlineData(
        typeof(ResourceDeploymentAppliedEvent),
        "pole.resource-deployments/v1/resource-deployment-applied")]
    [InlineData(
        typeof(ResourceDeploymentFailedEvent),
        "pole.resource-deployments/v1/resource-deployment-failed")]
    [InlineData(
        typeof(ResourceDeploymentDeletedEvent),
        "pole.resource-deployments/v1/resource-deployment-deleted")]
    public void Events_HaveStablePersistedNames(
        Type eventType,
        string expectedName)
    {
        var entityName = eventType.GetCustomAttribute<EntityNameAttribute>();

        Assert.NotNull(entityName);
        Assert.Equal(expectedName, entityName.EntityName);
    }

    [Fact]
    public void RequestEvent_RoundTripsOnlyDurableIdentifiers()
    {
        var @event = new ResourceDeploymentReconciliationRequestedEvent(
            DeploymentId,
            RuntimeCellId,
            Generation: 4,
            ResourceRevision: 7)
        {
            EventCore = EventCore.Create(TenantId)
        };

        var json = JsonSerializer.Serialize(@event, EventJson.SerializerOptions);
        using var payload = JsonDocument.Parse(json);
        var root = payload.RootElement;

        Assert.Equal(DeploymentId, root.GetProperty("resourceDeploymentId").GetString());
        Assert.Equal(RuntimeCellId, root.GetProperty("runtimeCellId").GetString());
        Assert.Equal(7, root.GetProperty("resourceRevision").GetInt64());
        Assert.Equal(TenantId, root.GetProperty("eventCore").GetProperty("tenantId").GetString());
        Assert.False(root.TryGetProperty("kubernetesNamespace", out _));
        Assert.False(root.TryGetProperty("deploymentTargetId", out _));
        Assert.False(root.TryGetProperty("credentials", out _));
        Assert.False(root.TryGetProperty("spec", out _));
    }

    [Fact]
    public void PendingKeys_AreOperationAndGenerationSpecific()
    {
        Assert.Equal(
            $"resource-deployment:{DeploymentId}:3:apply",
            ResourceDeploymentPendingKey.Apply(DeploymentId, 3));
        Assert.Equal(
            $"resource-deployment:{DeploymentId}:3:delete",
            ResourceDeploymentPendingKey.Delete(DeploymentId, 3));
    }

    private sealed class FakeAdapter : IResourceDeploymentAdapter
    {
        public Task<ResourceDeploymentAdapterResult> ApplyAsync(
            ResourceDeploymentReconciliationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ResourceDeploymentAdapterResult>(
                ResourceDeploymentAdapterResult.Succeeded.Create());

        public Task<ResourceDeploymentAdapterResult> DeleteAsync(
            ResourceDeploymentReconciliationContext context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ResourceDeploymentAdapterResult>(
                ResourceDeploymentAdapterResult.Succeeded.Create());
    }
}
