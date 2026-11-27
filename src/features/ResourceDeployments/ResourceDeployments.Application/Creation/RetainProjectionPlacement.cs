// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ResourceDeployments.Application.Common;
using Pole.Features.ResourceDeployments.Application.Mongo;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.ResourceDeployments.Application.Creation;

internal sealed class MongoRetainProjectionPlacement(
    IMongoCollection<ResourceDeployment> deployments,
    IEnumerable<ResourceDeploymentAdapterDescriptor> registered,
    FenceRegisteredResource fenceResource,
    MongoCreateResourceDeploymentSet create
)
{
    public async Task<CreateResourceDeploymentOutcome> InvokeAsync(
        RetainProjectionPlacementRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        ResourceDeploymentPersistenceGuards.EnsureActiveTransaction(session, "Retained projection placement");
        var descriptor = registered.SingleOrDefault(r => r.ResourceType == request.ConsumerType);
        if (descriptor is null || !typeof(IProjectionOnlyPlacementAdapter).IsAssignableFrom(descriptor.AdapterType))
            return Unavailable(request.ConsumerId);
        var consumer = await fenceResource(
            new(request.TenantId, request.ConsumerType, request.ConsumerId),
            session,
            ct);
        var sourceOwner = await fenceResource(new(request.TenantId, request.SourceType, request.SourceId), session, ct);
        if (consumer is null || sourceOwner is null || consumer.ResourceGroupId != sourceOwner.ResourceGroupId)
            return Unavailable(request.ConsumerId);
        var source = await deployments.FindOneAndUpdateAsync(
            session,
            ResourceDeploymentFilters.ForExactDeployment(
                request.TenantId,
                request.SourceType,
                request.SourceId,
                request.SourceDeploymentId),
            Builders<ResourceDeployment>.Update.Inc(d => d.ReferenceFence, 1),
            cancellationToken: ct);
        if (source is null || source.DesiredOperation == ResourceDeploymentOperation.Delete ||
            source.State is PreparedResourceDeploymentState or DeletedResourceDeploymentState ||
            source.ResourceGroupId != consumer.ResourceGroupId)
            return Unavailable(request.ConsumerId);
        var placement = source.ToReconciliationSnapshot().Placement;
        var retained = new CreateResourceDeploymentRequest(
            request.DeploymentId,
            request.TenantId,
            request.ConsumerType,
            request.ConsumerId,
            source.ResourceGroupId,
            source.IsolationScopeId,
            new(placement.DeploymentTargetId, placement.TargetWorkloadConfigurationRevision, null),
            placement.Compute,
            null,
            descriptor.PlacementCapability,
            PinnedCompute: true);
        var result = await create.PersistSetAsync(new([retained]), session, ct, request.SourceType);
        return result switch
        {
            CreatedResourceDeployments success => success.Placements.Single(),
            CreateResourceDeploymentError error => error
        };
    }

    private static CreateResourceDeploymentError Unavailable(string id) => new(new ResourcePlacementUnavailable(id));
}
