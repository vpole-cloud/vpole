using MongoDB.Bson;
using Pole.Features.ResourceDeployments.Domain;

namespace Pole.Tests.Integration.GitOps;

internal static class GitOpsPlacementTestData
{
    public static ResourceDeployment AdditionalPlacement(ResourceDeployment existing)
    {
        if (existing.PlacementCapability != ResourcePlacementCapability.MultiTarget)
            throw new ArgumentException("Additional placements require a multi-target resource.", nameof(existing));

        var placement = existing.ResolvedPlacement with
        {
            RuntimeCellId = ObjectId.GenerateNewId().ToString(),
            DeploymentTargetId = ObjectId.GenerateNewId().ToString(),
        };
        var identity = ResourceDeploymentFactory.Create(
            existing.TenantId,
            ResourceTypeId.Create(existing.ResourceType),
            existing.ResourceId,
            existing.ResourceGroupId,
            existing.IsolationScopeId,
            placement,
            existing.PlacementCapability,
            runtimeCellReady: true,
            ResourceDeploymentFactory.DefaultOptions);
        return existing with
        {
            Id = identity.Id,
            RuntimeCellId = identity.RuntimeCellId,
            PlacementKey = identity.PlacementKey,
            ResolvedPlacement = placement,
        };
    }
}
