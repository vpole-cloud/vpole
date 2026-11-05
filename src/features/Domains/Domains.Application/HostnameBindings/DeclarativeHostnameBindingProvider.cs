using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Application.Lifecycle;
using Pole.Features.Domains.Application.DomainZones;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application;
using MongoDB.Driver;
using Pole.Database;
using Pole.Dns;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Workloads;
using System.Text.Json;

namespace Pole.Features.Domains.Application.HostnameBindings;

public sealed record HostnameBindingDeclaration(
    string Zone,
    ResourceAddress Resource,
    string DeploymentTarget,
    string RelativeName);

public sealed record HostnameDeclarationDependencyUnavailable : IDeclarationFailure;

public sealed record HostnameDeclarationAlreadyReserved : IDeclarationFailure;

internal sealed class DeclarativeHostnameBindingProvider(
    IMongoCollection<TenantDomainZone> zones,
    IMongoCollection<HostnameBinding> bindings,
    IGitOpsMutations git,
    MongoRunTransaction transactions,
    ResolveNamedDomainReservationTarget resolveTargetName,
    ResolveDomainReservationTargetPlanningSnapshot readTarget,
    FenceDomainReservationTargetPlanningSnapshot fenceTarget,
    CreateCustomHostnameBinding createBinding,
    FenceDomainZone fenceZone,
    FenceHostnameBinding fenceBinding,
    IEnumerable<IHostnameBindingWorkloadCapability> workloadCapabilities,
    MongoDeleteCustomHostnameBinding deletion)
    : ITransactionalDeclarativeResourceProvider, IDeclarativeReferencePreparation,
        IDeclarativeResourceLifecycleProvider, IDeclarativeAdoptionPreviewProvider, IDeclarativePreviewReferenceProvider
{
    private readonly IReadOnlyDictionary<string, IHostnameBindingWorkloadCapability> capabilities =
        workloadCapabilities.ToDictionary(item => item.Kind, StringComparer.Ordinal);

    private const string ResourceField = "spec.resource";

    private const string ZoneField = "spec.zone";

    public string Kind => "HostnameBinding";

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        HostnameDeclarationDependencyUnavailable => nameof(HostnameDeclarationDependencyUnavailable),
        HostnameDeclarationAlreadyReserved => nameof(HostnameDeclarationAlreadyReserved),
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<HostnameBindingDeclaration>(spec);
        DeclarationJson.Require(ResourceTechnicalName.IsValid(declaration.Zone), ZoneField);
        DeclarationJson.Require(ResourceTechnicalName.IsValid(declaration.DeploymentTarget),
            DeclarationFieldPaths.DeploymentTarget);
        DeclarationJson.Require(declaration.Resource is not null &&
            DeclarationNames.Valid(declaration.Resource.Name) &&
            !string.IsNullOrWhiteSpace(declaration.Resource.Kind) &&
            capabilities.ContainsKey(declaration.Resource.Kind), ResourceField);
        try
        {
            return DeclarationJson.Write(declaration with
            {
                RelativeName = RelativeDnsName.Create(declaration.RelativeName).Value
            });
        }
        catch (ArgumentException)
        {
            throw new DeclarationSchemaException("spec.relativeName");
        }
    }

    public DeclarationFacts Facts(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<HostnameBindingDeclaration>(spec);
        return new(null, [], [new(new("DomainZone", declaration.Zone), ZoneField),
            new(declaration.Resource, ResourceField)], [], [], ReferencesShareScope: false);
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec)
    {
        var declaration = DeclarationJson.Read<HostnameBindingDeclaration>(spec);
        return [new(DeclarationPhase.Bind, [new(new("DomainZone", declaration.Zone), DeclarationPhase.Prepare),
            new(declaration.Resource, DeclarationPhase.Configure)])];
    }

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) =>
        (await bindings.Find(session, DomainFilters.BindingByTechnicalName(request.TenantId, request.Name))
            .FirstOrDefaultAsync(cancellationToken))?.Id;

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken cancellationToken) =>
        git.ExecuteAsync(operation, async (session, ct) =>
        {
            var context = await ReadContextAsync(operation.Context.TenantId, operation.Declaration,
                operation.Input, session, ct);
            if (context is null)
                return new(operation.ResourceId, new HostnameDeclarationDependencyUnavailable());

            var current = await bindings.Find(session,
                DomainFilters.BindingById(operation.Context.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(ct);
            var created = current is null;
            if (created)
            {
                var spec = DeclarationJson.Read<HostnameBindingDeclaration>(operation.Declaration.Spec);
                var result = await createBinding(new(operation.ResourceId, operation.Context.TenantId,
                    context.Zone.Id, context.Zone.DeploymentTargetId, context.Workload.ResourceType,
                    context.Workload.ResourceId, context.Workload.DeploymentId, spec.RelativeName,
                    operation.Declaration.Address.Name), session, ct);
                if (result is CustomHostnameBindingError error)
                    return new(operation.ResourceId, error switch
                    {
                        CustomHostnameBindingNameConflict or CustomHostnameBindingZoneChanged =>
                            new DeclarationPreconditionsChanged(),
                        CustomHostnameBindingConflict => new HostnameDeclarationAlreadyReserved(),
                        _ => new HostnameDeclarationDependencyUnavailable()
                    });
            }

            var provenance = new HostnameAttachmentProvenance(operation.Context.SourceId,
                operation.Context.RunId, operation.Context.Authority.Commit);
            var attached = await context.Capability.ApplyAsync(
                new(context.Workload, operation.ResourceId, provenance), session, ct);
            return attached switch
            {
                HostnameAttachmentApplied applied => new(operation.ResourceId, Changed: created || applied.Changed),
                HostnameAttachmentPreconditionsChanged =>
                    new(operation.ResourceId, new DeclarationPreconditionsChanged()),
                HostnameAttachmentUnavailable or HostnameAttachmentPlacementUnavailable or
                    HostnameAttachmentBindingRejected =>
                    new(operation.ResourceId, new HostnameDeclarationDependencyUnavailable())
            };
        }, cancellationToken);

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
            new(DomainLifecycleVersions.ZoneRevisionKey(zone.Id), zone.Revision),
            new(DomainLifecycleVersions.ZoneReferenceFenceKey(zone.Id), zone.ReferenceFence),
            .. targetVersion.Documents,
            .. workload.DocumentVersions.Select(item => new DeclarationDocumentVersion(item.DocumentId, item.Version))
        ]);
        return new GitLifecycleAdoptionPreview(found, Project(zone.Id, zone.DeploymentTargetId, resourceId,
            workload.ResourceType.Value, workload.DeploymentId, spec.RelativeName, hostname, attached: true) with
        { Dependencies = dependencies }, resolved.References);
    }

    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Operation is { } operation
            ? await bindings.Find(session, DomainFilters.BindingById(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(cancellationToken) : null;
        current ??= await bindings.Find(session,
                DomainFilters.BindingByTechnicalName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();

        if (current.Identity is not NamedDomainIdentity name || name.TechnicalName != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var capability = capabilities.Values.SingleOrDefault(item => item.ResourceType.Value == current.ResourceType);
        if (capability is null || await capability.ReadAsync(new(request.TenantId, current.ResourceId,
                current.DeploymentTargetId, current.Hostname), session, cancellationToken)
                is not HostnameAttachmentSnapshot workload)
            return new DeclarationReadBlocked(new HostnameDeclarationDependencyUnavailable());

        return new CurrentDeclaration(current.Id, new([BindingVersion(current)]),
            Project(current.TenantDomainZoneId, current.DeploymentTargetId, current.ResourceId,
                current.ResourceType, current.ResourceDeploymentId, current.RelativeName, current.Hostname,
                workload.CurrentHostnames.Contains(current.Hostname, StringComparer.Ordinal)),
            DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var context = await ReadContextAsync(request.TenantId, request.Declaration, request.Operation,
            session, cancellationToken);
        if (context is null)
            return new DeclarationReadBlocked(new HostnameDeclarationDependencyUnavailable());

        return Project(context.Zone.Id, context.Zone.DeploymentTargetId, context.Workload.ResourceId,
            context.Workload.ResourceType.Value, context.Workload.DeploymentId, context.RelativeName,
            context.Workload.Hostname, attached: true) with
        { Dependencies = new(Versions(context)) };
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var operation = request.Operation;
        var context = await ReadContextAsync(operation.Context.TenantId, operation.Declaration,
            operation, session, cancellationToken);
        if (context is null || !Versions(context).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();

        var current = await bindings.Find(session,
                DomainFilters.BindingById(operation.Context.TenantId, operation.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        var expected = request.Expected.Documents.SingleOrDefault(item =>
            item.DocumentId == DomainLifecycleVersions.BindingReferenceFenceKey(operation.ResourceId));
        if (current is null ? expected is not null : expected != BindingVersion(current))
            return new DeclarationPreconditionsChanged();

        if (await fenceTarget(context.Target, session, cancellationToken)
                is not DomainReservationTargetPlanningFenced ||
            await fenceZone(new(context.Zone.TenantId, context.Zone.Id, context.Zone.Revision,
                context.Zone.ReferenceFence), session, cancellationToken) is not DomainZoneFenced)
            return new DeclarationPreconditionsChanged();

        if (current is not null && await fenceBinding(new(current.TenantId, current.Id,
                current.ReferenceFence), session, cancellationToken) is not HostnameBindingFenced)
            return new DeclarationPreconditionsChanged();

        return await context.Capability.FenceAsync(context.Workload, session, cancellationToken) switch
        {
            HostnameAttachmentFenced => new DeclarationFenced(),
            HostnameAttachmentPreconditionsChanged or HostnameAttachmentUnavailable or
                HostnameAttachmentPlacementUnavailable => new DeclarationPreconditionsChanged()
        };
    }

    private static DeclarationDocumentVersion BindingVersion(HostnameBinding binding) =>
        new(DomainLifecycleVersions.BindingReferenceFenceKey(binding.Id), binding.ReferenceFence);

    private static DeclarationDocumentVersion[] Versions(ResolvedHostname context) =>
    [
        new(DomainLifecycleVersions.ZoneRevisionKey(context.Zone.Id), context.Zone.Revision),
        new(DomainLifecycleVersions.ZoneReferenceFenceKey(context.Zone.Id), context.Zone.ReferenceFence),
        .. TargetVersions(context.Target),
        .. context.Workload.DocumentVersions.Select(item =>
            new DeclarationDocumentVersion(item.DocumentId, item.Version))
    ];

    private static DeclarationDocumentVersion[] TargetVersions(DomainReservationTargetPlanningSnapshot target) =>
    [
        new(DeploymentTargetVersionKeys.Revision(target.PublicIngress.DeploymentTargetId), target.TargetRevision),
        new(DeploymentTargetVersionKeys.WorkloadConfiguration(target.PublicIngress.DeploymentTargetId),
            target.PublicIngress.WorkloadConfigurationRevision)
    ];

    private static DeclarationProjection Project(
        string zoneId,
        string targetId,
        string resourceId,
        string resourceType,
        string deploymentId,
        string relativeName,
        string hostname,
        bool attached) => new(DeclarationJson.Write(new
        {
            zoneId,
            targetId,
            resourceId,
            resourceType,
            deploymentId,
            relativeName,
            hostname
        }), DeclarationJson.Write(new { attached }));

    public async Task<PrepareDeclarationReferencesOutcome> PrepareReferencesAsync(
        GitOperationInput operation,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<HostnameBindingDeclaration>(operation.Declaration.Spec);
        var address = new ResourceAddress("DeploymentTarget", spec.DeploymentTarget);
        var accepted = await transactions.InvokeOutcomeAsync<AcceptedTarget>(async (transaction, ct) =>
            transaction.Commit(new AcceptedTarget(await git.ResolveAcceptedAsync(
                new(operation.Context, operation.ResourceId, address), transaction.Session, ct))), cancellationToken);
        if (accepted.Id is not null)
            return new DeclarationReferencesPrepared();

        var found = await resolveTargetName(new(operation.Context.TenantId, address.Name), cancellationToken);
        if (found is not DeploymentTargetLookupDto target)
            return new DeclarationReadBlocked(new HostnameDeclarationDependencyUnavailable());

        var pinned = await git.PinReferenceAsync(new(operation.Context, operation.ResourceId,
            new(address, target.Id)), cancellationToken);
        return pinned switch
        {
            GitReferencePinned => new DeclarationReferencesPrepared(),
            StaleGitExecution stale => stale
        };
    }

    private async Task<ResolvedHostname?> ReadContextAsync(
        string tenantId,
        CompiledDeclaration declaration,
        GitOperationInput? operation,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<HostnameBindingDeclaration>(declaration.Spec);
        if (!capabilities.TryGetValue(spec.Resource.Kind, out var capability))
            return null;

        var zoneId = await ResolveReferenceAsync(tenantId, new("DomainZone", spec.Zone),
            operation, session, cancellationToken);
        var resourceId = await ResolveReferenceAsync(tenantId, spec.Resource, operation, session, cancellationToken);
        if (zoneId is null || resourceId is null)
            return null;

        var zone = await zones.Find(session, DomainFilters.ZoneById(tenantId, zoneId))
            .FirstOrDefaultAsync(cancellationToken);
        if (zone is null || zone.Identity is not NamedDomainIdentity name || name.TechnicalName != spec.Zone)
            return null;

        string? targetId;
        if (operation is not null)
            targetId = await ResolveReferenceAsync(tenantId, new("DeploymentTarget", spec.DeploymentTarget),
                operation, session, cancellationToken);
        else
            targetId = await resolveTargetName(new(tenantId, spec.DeploymentTarget), cancellationToken)
                is DeploymentTargetLookupDto target ? target.Id : null;
        if (targetId != zone.DeploymentTargetId ||
            await readTarget(new(tenantId, zone.DeploymentTargetId), session, cancellationToken)
                is not DomainReservationTargetPlanningSnapshot targetSnapshot)
            return null;

        string hostname;
        try
        {
            hostname = RelativeDnsName.Create(spec.RelativeName).Under(DnsName.Create(zone.Domain)).Value;
        }
        catch (ArgumentException)
        {
            return null;
        }

        var read = await capability.ReadAsync(new(tenantId, resourceId, zone.DeploymentTargetId, hostname),
            session, cancellationToken);
        return read is HostnameAttachmentSnapshot workload
            ? new(zone, targetSnapshot, workload, capability, spec.RelativeName) : null;
    }

    private Task<string?> ResolveReferenceAsync(
        string tenantId,
        ResourceAddress address,
        GitOperationInput? operation,
        IClientSessionHandle session,
        CancellationToken cancellationToken) => operation is null
        ? git.ResolveAsync(new(tenantId, address), session, cancellationToken)
        : git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId, address), session, cancellationToken);

    private sealed record ResolvedHostname(
        TenantDomainZone Zone,
        DomainReservationTargetPlanningSnapshot Target,
        HostnameAttachmentSnapshot Workload,
        IHostnameBindingWorkloadCapability Capability,
        string RelativeName);

    private sealed record AcceptedTarget(string? Id);
}
