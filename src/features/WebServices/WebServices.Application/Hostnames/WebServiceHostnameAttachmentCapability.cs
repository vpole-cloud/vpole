using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Domains.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Declarations;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Application.Releases;
using Pole.Features.WebServices.Domain;
using Pole.Workloads;

namespace Pole.Features.WebServices.Application.Hostnames;

internal sealed class WebServiceHostnameAttachmentCapability(
    IMongoCollection<WebService> services,
    LoadIsolationScopePlanningSnapshot loadScope,
    FenceIsolationScopePlanningSnapshot fenceScope,
    LoadResourceDeploymentPlanningSnapshot loadPlacements,
    FenceResourceDeploymentPlanningSnapshot fencePlacements,
    LoadHostnameBindingsForDeploymentInTransaction loadBindings,
    RequestResourceDeploymentSetApply applyPlacements,
    AdvanceResourceDeploymentRevision advanceRevision,
    PersistWebServiceHostnameAttachment persist,
    PersistCurrentWebServiceRelease persistRelease,
    TimeProvider clock
) : IHostnameBindingRemovalCapability
{
    public string Kind => "WebService";

    public ResourceTypeId ResourceType => WebServiceResourceDeployment.ResourceType;

    public async Task<ReadHostnameAttachmentOutcome> ReadAsync(
        ReadHostnameAttachmentRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var service = await services.Find(session, WebServiceFilters.ById(request.TenantId, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (service is null || service.Exposure.Mode != WebServiceExposureMode.Public ||
            service.Lifecycle is not (ActiveWebServiceLifecycle or AwaitingFirstArtifactWebServiceLifecycle))
            return new HostnameAttachmentUnavailable();

        var scopeRead = await loadScope(
            new IsolationScopePlanningRequest(request.TenantId, service.ResourceGroupId, service.IsolationScopeId),
            session,
            cancellationToken);
        if (scopeRead is not IsolationScopePlanningSnapshot scope)
            return new HostnameAttachmentUnavailable();

        var read = await loadPlacements(
            new ResourceDeploymentPlanningRequest(request.TenantId, ResourceType, request.ResourceId),
            session,
            cancellationToken);
        if (read is not ResourceDeploymentPlanningSnapshot placements || placements.Items.Any(item =>
                item.State == ResourceDeploymentPlanningState.Deleting))
            return new HostnameAttachmentPlacementUnavailable();

        var candidates = placements.Items
            .Where(item => item.DeploymentTargetId == request.DeploymentTargetId)
            .ToArray();
        if (candidates.Length != 1 || candidates[0].Ingress is null)
            return new HostnameAttachmentPlacementUnavailable();

        var placement = candidates[0];
        return new HostnameAttachmentSnapshot(
            request.TenantId,
            request.ResourceId,
            ResourceType,
            request.DeploymentTargetId,
            placement.DeploymentId,
            request.Hostname,
            placement.Ingress!.Hostnames.Order(StringComparer.Ordinal).ToArray(),
            [
                new HostnameAttachmentDocumentVersion(WebServiceDeclarations.VersionKey(service.Id), service.Revision),
                new HostnameAttachmentDocumentVersion(
                    ResourceGroupVersionKeys.Revision(scope.ResourceGroupId),
                    scope.ResourceGroupRevision),
                new HostnameAttachmentDocumentVersion(
                    ResourceGroupVersionKeys.IsolationScopeRevision(scope.IsolationScopeId),
                    scope.ScopeRevision),
                .. placements.Items.Select(item => new HostnameAttachmentDocumentVersion(
                    ResourceDeploymentVersionKeys.Revision(item.DeploymentId),
                    item.Revision)),
            ],
            service.Lifecycle is AwaitingFirstArtifactWebServiceLifecycle
                ? HostnameAttachmentAvailability.AwaitingArtifact
                : HostnameAttachmentAvailability.Active);
    }

    public async Task<ReadHostnameAttachmentRemovalOutcome> ReadRemovalAsync(
        ReadHostnameAttachmentRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await services.Find(session, WebServiceFilters.ById(request.TenantId, request.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new HostnameAttachmentOwnerGone();
        if (current.Lifecycle is DeletingWebServiceLifecycle)
            return new HostnameAttachmentOwnerDeleting();

        return await ReadAsync(request, session, cancellationToken) switch
        {
            HostnameAttachmentSnapshot snapshot => snapshot,
            HostnameAttachmentUnavailable unavailable => unavailable,
            HostnameAttachmentPlacementUnavailable unavailable => unavailable,
        };
    }

    public async Task<FenceHostnameAttachmentOutcome> FenceAsync(
        HostnameAttachmentSnapshot snapshot,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var read = await ReadAsync(
            new ReadHostnameAttachmentRequest(
                snapshot.TenantId,
                snapshot.ResourceId,
                snapshot.DeploymentTargetId,
                snapshot.Hostname),
            session,
            cancellationToken);
        if (read is HostnameAttachmentUnavailable unavailable)
            return unavailable;
        if (read is HostnameAttachmentPlacementUnavailable placementUnavailable)
            return placementUnavailable;

        var current = (HostnameAttachmentSnapshot)read.Value;
        if (current.DeploymentId != snapshot.DeploymentId ||
            !current.DocumentVersions.SequenceEqual(snapshot.DocumentVersions))
            return new HostnameAttachmentPreconditionsChanged();

        var resource = await services.Find(session, WebServiceFilters.ById(snapshot.TenantId, snapshot.ResourceId))
            .SingleAsync(cancellationToken);
        var scopeRead = await loadScope(
            new IsolationScopePlanningRequest(resource.TenantId, resource.ResourceGroupId, resource.IsolationScopeId),
            session,
            cancellationToken);
        if (scopeRead is not IsolationScopePlanningSnapshot scope ||
            await fenceScope(scope, session, cancellationToken) is not IsolationScopePlanningFenced)
            return new HostnameAttachmentPreconditionsChanged();

        var version = snapshot.DocumentVersions.Single(item =>
            item.DocumentId == WebServiceDeclarations.VersionKey(snapshot.ResourceId));
        var filter = WebServiceFilters.AtRevision(snapshot.TenantId, snapshot.ResourceId, version.Version);
        var write = await services.UpdateOneAsync(
            session,
            filter,
            Builders<WebService>.Update.Inc(item => item.PlanningFence, 1),
            cancellationToken: cancellationToken);
        if (write.ModifiedCount != 1)
            return new HostnameAttachmentPreconditionsChanged();

        var placementRead = await loadPlacements(
            new ResourceDeploymentPlanningRequest(snapshot.TenantId, ResourceType, snapshot.ResourceId),
            session,
            cancellationToken);
        if (placementRead is not ResourceDeploymentPlanningSnapshot placements ||
            await fencePlacements(placements, session, cancellationToken) is not ResourceDeploymentPlanningFenced)
            return new HostnameAttachmentPreconditionsChanged();

        return new HostnameAttachmentFenced();
    }

    public Task<ApplyHostnameAttachmentOutcome> ApplyAsync(
        ApplyHostnameAttachmentRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) => ApplyAuthoritativeAliasesAsync(
        new ChangeHostnameAttachment(
            request.Snapshot,
            request.BindingId,
            request.Provenance,
            HostnameAttachmentChange.Attach),
        session,
        cancellationToken);

    public Task<ApplyHostnameAttachmentOutcome> RemoveAsync(
        RemoveHostnameAttachmentRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) => ApplyAuthoritativeAliasesAsync(
        new ChangeHostnameAttachment(
            request.Snapshot,
            request.BindingId,
            request.Provenance,
            HostnameAttachmentChange.Remove),
        session,
        cancellationToken);

    private async Task<ApplyHostnameAttachmentOutcome> ApplyAuthoritativeAliasesAsync(
        ChangeHostnameAttachment request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var expected = request.Snapshot;
        var read = await ReadAsync(
            new ReadHostnameAttachmentRequest(
                expected.TenantId,
                expected.ResourceId,
                expected.DeploymentTargetId,
                expected.Hostname),
            session,
            cancellationToken);
        if (read is HostnameAttachmentUnavailable unavailable)
            return unavailable;
        if (read is HostnameAttachmentPlacementUnavailable placementUnavailable)
            return placementUnavailable;

        var snapshot = (HostnameAttachmentSnapshot)read.Value;
        if (snapshot.DeploymentId != expected.DeploymentId ||
            !snapshot.DocumentVersions.SequenceEqual(expected.DocumentVersions))
            return new HostnameAttachmentPreconditionsChanged();

        var bindings = await loadBindings(
            new LoadHostnameBindingsForDeploymentInTransactionRequest(
                expected.TenantId,
                ResourceType,
                expected.ResourceId,
                expected.DeploymentId),
            session,
            cancellationToken);
        var bindingPresent = bindings.Any(item => item.Id == request.BindingId &&
            item.Hostname == expected.Hostname && item.Kind == HostnameBindingKinds.Custom &&
            item.DeploymentTargetId == expected.DeploymentTargetId);
        if (request.Change == HostnameAttachmentChange.Attach
                ? !bindingPresent
                : bindings.Any(item => item.Id == request.BindingId))
            return new HostnameAttachmentBindingRejected();

        var hostnames = bindings.Select(item => item.Hostname).Distinct().Order(StringComparer.Ordinal).ToArray();
        if (hostnames.SequenceEqual(snapshot.CurrentHostnames))
            return new HostnameAttachmentApplied(false);

        var service = await services.Find(session, WebServiceFilters.ById(expected.TenantId, expected.ResourceId))
            .SingleAsync(cancellationToken);
        var placementRead = await loadPlacements(
            new ResourceDeploymentPlanningRequest(expected.TenantId, ResourceType, expected.ResourceId),
            session,
            cancellationToken);
        if (placementRead is not ResourceDeploymentPlanningSnapshot placements)
            return new HostnameAttachmentPlacementUnavailable();
        var placement = placements.Items.Single(item => item.DeploymentId == expected.DeploymentId);
        var next = service.AdvancePlacementDesiredState();
        var ingress = placement.Ingress!;
        var updated = await applyPlacements(
            new ResourceDeploymentSetApplyRequest(
                expected.TenantId,
                ResourceType,
                expected.ResourceId,
                next.ActiveRevision,
                [
                    new ResourceDeploymentSetApplyItem(
                        placement.DeploymentId,
                        placement.Revision,
                        new ComputeProfileReference(
                            placement.Compute.ComputeProfileId,
                            placement.Compute.ComputeProfileRevision),
                        new ReplaceIngress(
                            ResourceDeploymentIngressConfiguration.Create(
                                ingress.IngressClassName,
                                ingress.ClusterIssuerName,
                                ingress.PublicBaseDomain,
                                hostnames))),
                ],
                service.Lifecycle is AwaitingFirstArtifactWebServiceLifecycle
                    ? ResourceDeploymentActivation.KeepPrepared
                    : ResourceDeploymentActivation.Reconcile),
            session,
            cancellationToken);
        if (updated is not MutatedResourceDeployments)
            return new HostnameAttachmentPreconditionsChanged();

        var advanced = await advanceRevision(
            new ResourceDeploymentResourceRevisionRequest(
                expected.TenantId,
                ResourceType,
                expected.ResourceId,
                next.ActiveRevision),
            session,
            cancellationToken);
        if (advanced is not MutatedResourceDeployments changed)
            return new HostnameAttachmentPreconditionsChanged();

        var result = await persist(
            new WebServiceHostnameAttachmentWrite(
                service,
                next,
                next.ToUpdatedEvent(WebServiceDeploymentWorkflow.ToEventDeployments(changed.Placements)),
                request.Change == HostnameAttachmentChange.Attach
                    ? new AttachedWebServiceHostnameBinding(request.BindingId)
                    : new RemovedWebServiceHostnameBinding(request.BindingId, expected)),
            session,
            cancellationToken);
        if (result is not WebServiceUpdateAccepted)
            return new HostnameAttachmentPreconditionsChanged();

        if (next.Lifecycle is ActiveWebServiceLifecycle)
        {
            var release = WebServiceReleaseFactory.CreatePending(
                next.TenantId,
                next.Id,
                next.ActiveRevision,
                WebServiceReleaseAction.HostnameChanged,
                null,
                null,
                next.Spec,
                WebServiceDeploymentWorkflow.ToReleaseDeployments(changed.Placements),
                null,
                null,
                new WebServiceReleaseFactory.CreateOptions(
                    ObjectId.GenerateNewId().ToString(),
                    clock.GetUtcNow().UtcDateTime),
                next.Exposure,
                new GitOpsReleaseProvenance(
                    request.Provenance.SourceId,
                    request.Provenance.RunId,
                    request.Provenance.Commit,
                    service.Spec.Image.ResolvedImage?.Tag,
                    next.Spec.Image.ResolvedImage!.Tag));
            await persistRelease(session, release, cancellationToken);
        }

        return new HostnameAttachmentApplied(true);
    }

    private enum HostnameAttachmentChange { Attach, Remove }

    private sealed record ChangeHostnameAttachment(
        HostnameAttachmentSnapshot Snapshot,
        string BindingId,
        HostnameAttachmentProvenance Provenance,
        HostnameAttachmentChange Change
    );
}
