using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;

namespace Pole.Features.Domains.Application.Persistence.DomainZones;

internal sealed partial class DeclarativeDomainZoneProvider
{
    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var current = await zones.Find(session, DomainFilters.ZoneById(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new GitLifecycleResourceAbsent();

        if (current.Kind == TenantDomainZoneKind.Generated || current.Identity is not NamedDomainIdentity name)
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());
        if (request.Resource.Address.Kind != Kind || name.TechnicalName != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        return new GitLifecycleResourceSnapshot(request.Resource, DomainLifecycleVersions.Zone(current),
            GitLifecyclePresence.Active, GitLifecycleProtection.ConfigurationOnly,
            GitLifecycleVersionSemantics.ObservedDocuments);
    }

    public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var read = await ReadLifecycleAsync(new(request.Authority.TenantId, request.Resource),
            session, cancellationToken);
        if (read is GitLifecycleResourceAbsent)
            return new GitLifecycleResourceRemoved();

        if (read is DeclarationReadBlocked blocked)
            return blocked;
        if (!DomainLifecycleVersions.Same(((GitLifecycleResourceSnapshot)read.Value).Version, request.Expected))
            return new DeclarationPreconditionsChanged();

        var result = await deletion.ExecuteAsync(new(request.Authority.TenantId, request.Resource.ResourceId,
            request.Authority), session, cancellationToken);
        return result switch
        {
            ManagedDomainZoneDeleted or DomainZoneNotFound => new GitLifecycleResourceRemoved(),
            GeneratedDomainZoneDeletionRejected => new DeclarationReadBlocked(new GitLifecycleResourceProtected()),
            DomainZoneHasHostnameBindings or ManagedDomainZoneReferenceRemovalRejected =>
                new DeclarationReadBlocked(new GitLifecycleDependencyRetained()),
            DomainZoneDeletionConflict => new DeclarationPreconditionsChanged(),
            DomainZoneGitOwned => new StaleGitExecution()
        };
    }

    public async Task<ReadPreviewReferencesOutcome> ReadPreviewReferencesAsync(
        ReadPreviewReferencesRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<DomainZoneDeclaration>(request.Consumer.Spec);
        var address = new ResourceAddress("DeploymentTarget", spec.DeploymentTarget);
        var accepted = request.References.SingleOrDefault(item => item.Address == address);
        var targetId = accepted?.ResourceId;
        if (targetId is null)
            targetId = await resolveTargetName(new(request.TenantId, spec.DeploymentTarget), cancellationToken)
                is DeploymentTargetLookupDto found ? found.Id : null;
        if (targetId is null || await readTarget(new(request.TenantId, targetId), session, cancellationToken)
                is not DomainReservationTargetPlanningSnapshot target)
            return new DeclarationReadBlocked(new DomainZoneDeclarationTargetUnavailable());
        return new GitPreviewReferencesSnapshot(request.References.Where(item => item.Address != address)
            .Append(new(address, targetId, new(TargetVersions(target)))).ToArray());
    }

    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(new(request.TenantId, request.Declaration.Address), session,
            cancellationToken);
        if (current is not CurrentDeclaration found || found.ResourceId != request.ResourceId)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var read = await ReadPreviewReferencesAsync(new(request.TenantId, request.Declaration, request.References),
            session, cancellationToken);
        if (read is DeclarationReadBlocked blocked)
            return blocked;

        var references = ((GitPreviewReferencesSnapshot)read.Value).References;
        var spec = DeclarationJson.Read<DomainZoneDeclaration>(request.Declaration.Spec);
        var target = references.Single(item => item.Address == new ResourceAddress("DeploymentTarget",
            spec.DeploymentTarget));
        return new GitLifecycleAdoptionPreview(found, Project(target.ResourceId, spec.Label) with
        { Dependencies = target.Version }, references);
    }
}
