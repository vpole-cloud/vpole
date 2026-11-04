using MongoDB.Driver;
using Pole.Dns;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Workloads;

namespace Pole.Features.Domains.Application.Persistence.HostnameBindings;

internal sealed partial class DeclarativeHostnameBindingProvider
{
    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var current = await bindings.Find(session,
            DomainFilters.BindingById(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new GitLifecycleResourceAbsent();

        if (current.Kind == HostnameBindingKind.Generated || current.Identity is not NamedDomainIdentity name)
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());
        if (request.Resource.Address.Kind != Kind || name.TechnicalName != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        return new GitLifecycleResourceSnapshot(request.Resource, DomainLifecycleVersions.Binding(current),
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

        if (request.Authority.Commit is not { } commit)
            return new DeclarationReadBlocked(new GitLifecycleUnsupported());

        var current = await bindings.Find(session,
            DomainFilters.BindingById(request.Authority.TenantId, request.Resource.ResourceId))
            .SingleAsync(cancellationToken);
        if (capabilities.Values.SingleOrDefault(item => item.ResourceType.Value == current.ResourceType)
                is not IHostnameBindingRemovalCapability capability)
            return new DeclarationReadBlocked(new GitLifecycleUnsupported());
        var lookup = new ReadHostnameAttachmentRequest(request.Authority.TenantId, current.ResourceId,
            current.DeploymentTargetId, current.Hostname);
        var attachment = await capability.ReadRemovalAsync(lookup, session, cancellationToken);
        if (attachment is HostnameAttachmentUnavailable or HostnameAttachmentPlacementUnavailable)
            return new DeclarationReadBlocked(new HostnameDeclarationDependencyUnavailable());

        if (attachment is HostnameAttachmentSnapshot active)
        {
            if (await capability.FenceAsync(active, session, cancellationToken) is not HostnameAttachmentFenced)
                return new DeclarationPreconditionsChanged();
            attachment = await capability.ReadRemovalAsync(lookup, session, cancellationToken);
            if (attachment is not HostnameAttachmentSnapshot)
                return new DeclarationPreconditionsChanged();
        }
        var deleted = await deletion.ExecuteAsync(new(new DeleteCustomHostnameBindingRequest(request.Authority.TenantId,
            ResourceTypeId.Create(current.ResourceType), current.ResourceId, current.ResourceDeploymentId, current.Id),
            request.Authority), session, cancellationToken);
        if (deleted is CustomHostnameBindingGitOwned)
            return new StaleGitExecution();

        if (deleted is CustomHostnameBindingError)
            return new DeclarationPreconditionsChanged();
        if (attachment is HostnameAttachmentSnapshot snapshot)
        {
            var result = await capability.RemoveAsync(new(snapshot, current.Id,
                new(request.Authority.SourceId, request.Authority.RunId, commit)),
                session, cancellationToken);
            return result switch
            {
                HostnameAttachmentApplied => new GitLifecycleResourceRemoved(),
                HostnameAttachmentPreconditionsChanged => new DeclarationPreconditionsChanged(),
                HostnameAttachmentUnavailable or HostnameAttachmentPlacementUnavailable or
                    HostnameAttachmentBindingRejected
                    => new DeclarationReadBlocked(new HostnameDeclarationDependencyUnavailable())
            };
        }
        return new GitLifecycleResourceRemoved();
    }

    public async Task<ReadPreviewReferencesOutcome> ReadPreviewReferencesAsync(
        ReadPreviewReferencesRequest request, IClientSessionHandle session, CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<HostnameBindingDeclaration>(request.Consumer.Spec);
        var address = new ResourceAddress("DeploymentTarget", spec.DeploymentTarget);
        var targetId = request.References.SingleOrDefault(item => item.Address == address)?.ResourceId;
        if (targetId is null)
            targetId = await resolveTargetName(new(request.TenantId, spec.DeploymentTarget), cancellationToken)
                is DeploymentTargetLookupDto found ? found.Id : null;
        if (targetId is null || await readTarget(new(request.TenantId, targetId), session, cancellationToken)
                is not DomainReservationTargetPlanningSnapshot target)
            return new DeclarationReadBlocked(new HostnameDeclarationDependencyUnavailable());
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

        var spec = DeclarationJson.Read<HostnameBindingDeclaration>(request.Declaration.Spec);
        if (!capabilities.TryGetValue(spec.Resource.Kind, out var capability))
            return new DeclarationReadBlocked(new HostnameDeclarationDependencyUnavailable());

        var zoneId = request.References.SingleOrDefault(item => item.Address ==
            new ResourceAddress("DomainZone", spec.Zone))?.ResourceId;
        var resourceId = request.References.SingleOrDefault(item => item.Address == spec.Resource)?.ResourceId;
        if (zoneId is null || resourceId is null)
            return new DeclarationReadBlocked(new HostnameDeclarationDependencyUnavailable());

        var zone = await zones.Find(session, DomainFilters.ZoneById(request.TenantId, zoneId))
            .FirstOrDefaultAsync(cancellationToken);
        var references = await ReadPreviewReferencesAsync(
            new(request.TenantId, request.Declaration, request.References), session, cancellationToken);
        if (zone is null || references is not GitPreviewReferencesSnapshot resolved ||
            !resolved.References.Any(item => item.Address ==
                new ResourceAddress("DeploymentTarget", spec.DeploymentTarget) &&
                item.ResourceId == zone.DeploymentTargetId))
            return new DeclarationReadBlocked(new HostnameDeclarationDependencyUnavailable());
        var hostname = RelativeDnsName.Create(spec.RelativeName).Under(DnsName.Create(zone.Domain)).Value;
        if (await capability.ReadAsync(new(request.TenantId, resourceId, zone.DeploymentTargetId, hostname),
                session, cancellationToken) is not HostnameAttachmentSnapshot workload)
            return new DeclarationReadBlocked(new HostnameDeclarationDependencyUnavailable());
        var target = resolved.References.Single(item => item.Address ==
            new ResourceAddress("DeploymentTarget", spec.DeploymentTarget));
        if (target.Version is not { } targetVersion)
            return new DeclarationReadBlocked(new HostnameDeclarationDependencyUnavailable());

        var dependencies = new DeclarationVersionToken(
        [
            new($"DomainZone/{zone.Id}", zone.Revision),
            new($"DomainZone/{zone.Id}/ReferenceFence", zone.ReferenceFence),
            .. targetVersion.Documents,
            .. workload.DocumentVersions.Select(item => new DeclarationDocumentVersion(item.DocumentId, item.Version))
        ]);
        return new GitLifecycleAdoptionPreview(found, Project(zone.Id, zone.DeploymentTargetId, resourceId,
            workload.ResourceType.Value, workload.DeploymentId, spec.RelativeName, hostname, attached: true) with
        { Dependencies = dependencies }, resolved.References);
    }
}
