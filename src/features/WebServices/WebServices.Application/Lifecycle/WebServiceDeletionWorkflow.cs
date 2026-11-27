// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Lifecycle;

public sealed record WebServiceDeletionApplied(
    IReadOnlyList<PlacementSummaryDto> Deployments
);

public union ApplyWebServiceDeletionOutcome(
    WebServiceDeletionApplied,
    WebServiceUpdateConflict,
    WebServiceHostnameAttachmentRemovalRequired,
    SourceBuildWebServiceHasActiveJobs,
    ResourceDeploymentMutationError);

public sealed record ApplyWebServiceDeletionRequest(
    WebService Current,
    ResourceDeploymentSetDeletionRequest Deployments,
    GitLifecycleAuthority? Lifecycle = null,
    IReadOnlyList<ResourceConnectionPlanningVersion>? ReviewedConnections = null
);

public delegate Task<ApplyWebServiceDeletionOutcome> ApplyWebServiceDeletion(
    ApplyWebServiceDeletionRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

// Quota admission/release belongs to the caller and shares this same session.
internal sealed class WebServiceDeletionWorkflow(
    MongoBeginWebServiceDeletion deletion,
    EnsureNoWebServiceActiveSourceBuilds ensureNoActiveBuilds,
    RequestResourceDeploymentSetDeletion deleteDeployments,
    TimeProvider clock,
    EnsureConsumerSecretBindingRemoval ensureBindingsRemovable
)
{
    public async Task<ApplyWebServiceDeletionOutcome> ExecuteAsync(
        ApplyWebServiceDeletionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Current;
        var bindings = await ensureBindingsRemovable(
            new EnsureConsumerSecretBindingRemovalRequest(
                current.TenantId,
                WebServiceResourceDeployment.ResourceType,
                current.Id),
            session,
            cancellationToken);
        if (bindings is ConsumerSecretBindingsRequireRemoval)
            return new WebServiceUpdateConflict(
                current.Id,
                WebServiceUpdateConflictReason.SecretBindingsRequireRemoval);
        if (bindings is ConsumerSecretBindingDeletionChanged)
            return new WebServiceUpdateConflict(current.Id);

        var started = await deletion.ExecuteAsync(
            new BeginWebServiceDeletionRequest(
                current,
                current.BeginDeletion(clock.GetUtcNow().UtcDateTime),
                request.Lifecycle,
                request.ReviewedConnections),
            session,
            cancellationToken);
        if (started is WebServiceUpdateConflict conflict)
            return conflict;
        if (started is WebServiceHostnameAttachmentRemovalRequired attachment)
            return attachment;

        var builds = await ensureNoActiveBuilds(
            new EnsureNoWebServiceActiveSourceBuildsRequest(current.TenantId, current.Id),
            session,
            cancellationToken);
        if (builds is SourceBuildWebServiceHasActiveJobs active)
            return active;

        var deployments = await deleteDeployments(request.Deployments, session, cancellationToken);
        return deployments switch
        {
            MutatedResourceDeployments applied => new WebServiceDeletionApplied(applied.Placements),
            ResourceDeploymentMutationError rejected => rejected,
        };
    }
}
