// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using MongoDB.Driver;
using Pole.Database;
using Pole.Dns;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Application.Lifecycle;
using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Workloads;

namespace Pole.Features.Domains.Application.DomainZones;

public sealed record DomainZoneDeclaration(
    string DeploymentTarget,
    string Label
);

public sealed record DomainZoneDeclarationTargetUnavailable : IDeclarationFailure;

public sealed record DomainZoneDeclarationTargetAmbiguous : IDeclarationFailure;

public sealed record DomainZoneDeclarationAlreadyReserved : IDeclarationFailure;

internal sealed class DeclarativeDomainZoneProvider(
    IMongoCollection<TenantDomainZone> zones,
    IGitOpsMutations git,
    MongoRunTransaction transactions,
    ResolveNamedDomainReservationTarget resolveTargetName,
    ResolveDomainReservationTargetPlanningSnapshot readTarget,
    FenceDomainReservationTargetPlanningSnapshot fenceTarget,
    CreateManagedDomainZoneReservation createZone,
    FenceDomainZone fenceZone,
    MongoDeleteManagedDomainZone deletion
) : ITransactionalDeclarativeResourceProvider, IDeclarativeReferencePreparation, IDeclarativeResourceLifecycleProvider,
    IDeclarativeAdoptionPreviewProvider, IDeclarativePreviewReferenceProvider
{
    public async Task<PreviewLifecycleAdoptionOutcome> PreviewAdoptionAsync(
        PreviewLifecycleAdoptionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await ReadCurrentAsync(
            new ReadCurrentDeclarationRequest(request.TenantId, request.Declaration.Address),
            session,
            cancellationToken);
        if (current is not CurrentDeclaration found || found.ResourceId != request.ResourceId)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        var read = await ReadPreviewReferencesAsync(
            new ReadPreviewReferencesRequest(request.TenantId, request.Declaration, request.References),
            session,
            cancellationToken);
        if (read is DeclarationReadBlocked blocked)
            return blocked;

        var references = ((GitPreviewReferencesSnapshot)read.Value).References;
        var spec = DeclarationJson.Read<DomainZoneDeclaration>(request.Declaration.Spec);
        var target = references.Single(item =>
            item.Address == new ResourceAddress("DeploymentTarget", spec.DeploymentTarget));
        return new GitLifecycleAdoptionPreview(
            found,
            Project(target.ResourceId, spec.Label) with
            {
                Dependencies = target.Version,
            },
            references);
    }

    public async Task<ReadPreviewReferencesOutcome> ReadPreviewReferencesAsync(
        ReadPreviewReferencesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<DomainZoneDeclaration>(request.Consumer.Spec);
        var address = new ResourceAddress("DeploymentTarget", spec.DeploymentTarget);
        var accepted = request.References.SingleOrDefault(item => item.Address == address);
        var targetId = accepted?.ResourceId;
        if (targetId is null)
            targetId = await resolveTargetName(
                new NamedDomainReservationTargetRequest(request.TenantId, spec.DeploymentTarget),
                cancellationToken) is DeploymentTargetLookupDto found
                ? found.Id
                : null;
        if (targetId is null || await readTarget(
                new DomainReservationTargetPlanningRequest(request.TenantId, targetId),
                session,
                cancellationToken) is not DomainReservationTargetPlanningSnapshot target)
            return new DeclarationReadBlocked(new DomainZoneDeclarationTargetUnavailable());
        return new GitPreviewReferencesSnapshot(
            request.References
                .Where(item => item.Address != address)
                .Append(
                    new GitPreviewResolvedReference(
                        address,
                        targetId,
                        new DeclarationVersionToken(TargetVersions(target))))
                .ToArray());
    }

    public async Task<PrepareDeclarationReferencesOutcome> PrepareReferencesAsync(
        GitOperationInput operation,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<DomainZoneDeclaration>(operation.Declaration.Spec);
        var address = new ResourceAddress("DeploymentTarget", spec.DeploymentTarget);
        var accepted = await transactions.InvokeOutcomeAsync<AcceptedTarget>(
            async (transaction, ct) => transaction.Commit(
                new AcceptedTarget(
                    await git.ResolveAcceptedAsync(
                        new ResolveAcceptedGitReferenceRequest(operation.Context, operation.ResourceId, address),
                        transaction.Session,
                        ct))),
            cancellationToken);
        if (accepted.Id is not null)
            return new DeclarationReferencesPrepared();

        var found = await resolveTargetName(
            new NamedDomainReservationTargetRequest(operation.Context.TenantId, address.Name),
            cancellationToken);
        if (found is not DeploymentTargetLookupDto target)
            return new DeclarationReadBlocked(
                found is NamedDeploymentTargetAmbiguous
                    ? new DomainZoneDeclarationTargetAmbiguous()
                    : new DomainZoneDeclarationTargetUnavailable());

        var pinned = await git.PinReferenceAsync(
            new PinGitReferenceRequest(
                operation.Context,
                operation.ResourceId,
                new GitReferenceTarget(address, target.Id)),
            cancellationToken);
        return pinned switch
        {
            GitReferencePinned => new DeclarationReferencesPrepared(),
            StaleGitExecution stale => stale,
        };
    }

    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await zones.Find(session, DomainFilters.ZoneById(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new GitLifecycleResourceAbsent();

        if (current.Kind == TenantDomainZoneKind.Generated || current.Identity is not NamedDomainIdentity name)
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());
        if (request.Resource.Address.Kind != Kind || name.TechnicalName != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());
        return new GitLifecycleResourceSnapshot(
            request.Resource,
            DomainLifecycleVersions.Zone(current),
            GitLifecyclePresence.Active,
            GitLifecycleProtection.ConfigurationOnly,
            GitLifecycleVersionSemantics.ObservedDocuments);
    }

    public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var read = await ReadLifecycleAsync(
            new ReadLifecycleResourceRequest(request.Authority.TenantId, request.Resource),
            session,
            cancellationToken);
        if (read is GitLifecycleResourceAbsent)
            return new GitLifecycleResourceRemoved();

        if (read is DeclarationReadBlocked blocked)
            return blocked;
        if (!DomainLifecycleVersions.Same(((GitLifecycleResourceSnapshot)read.Value).Version, request.Expected))
            return new DeclarationPreconditionsChanged();

        var result = await deletion.ExecuteAsync(
            new RemoveManagedDomainZoneRequest(
                request.Authority.TenantId,
                request.Resource.ResourceId,
                request.Authority),
            session,
            cancellationToken);
        return result switch
        {
            ManagedDomainZoneDeleted or DomainZoneNotFound => new GitLifecycleResourceRemoved(),
            GeneratedDomainZoneDeletionRejected => new DeclarationReadBlocked(new GitLifecycleResourceProtected()),
            DomainZoneHasHostnameBindings or ManagedDomainZoneReferenceRemovalRejected => new DeclarationReadBlocked(
                new GitLifecycleDependencyRetained()),
            DomainZoneDeletionConflict => new DeclarationPreconditionsChanged(),
            DomainZoneGitOwned => new StaleGitExecution(),
        };
    }

    public string Kind => "DomainZone";

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        DomainZoneDeclarationTargetUnavailable => nameof(DomainZoneDeclarationTargetUnavailable),
        DomainZoneDeclarationTargetAmbiguous => nameof(DomainZoneDeclarationTargetAmbiguous),
        DomainZoneDeclarationAlreadyReserved => nameof(DomainZoneDeclarationAlreadyReserved),
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure)),
    };

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<DomainZoneDeclaration>(spec);
        DeclarationJson.Require(
            ResourceTechnicalName.IsValid(declaration.DeploymentTarget),
            DeclarationFieldPaths.DeploymentTarget);
        DeclarationJson.Require(
            DnsLabel.IsValid(declaration.Label) && TenantDomainZoneLabelPolicy.IsManagedLabelAllowed(declaration.Label),
            "spec.label");
        return DeclarationJson.Write(declaration with { Label = DnsLabel.Create(declaration.Label).Value });
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec) =>
        [new(DeclarationPhase.Prepare, [])];

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) => (await zones
        .Find(session, DomainFilters.ZoneByTechnicalName(request.TenantId, request.Name))
        .FirstOrDefaultAsync(cancellationToken))?.Id;

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken cancellationToken) =>
        git.ExecuteAsync(
            operation,
            async (session, ct) =>
            {
                var target = await ReadTargetAsync(
                    operation.Context.TenantId,
                    operation.Declaration,
                    operation.Input,
                    session,
                    ct);
                if (target is null)
                    return new GitOperationResult(operation.ResourceId, new DomainZoneDeclarationTargetUnavailable());

                var spec = DeclarationJson.Read<DomainZoneDeclaration>(operation.Declaration.Spec);
                var created = await createZone(
                    new CreateManagedDomainZoneCommand(
                        operation.ResourceId,
                        operation.Context.TenantId,
                        target.PublicIngress.DeploymentTargetId,
                        operation.Declaration.Address.Name,
                        spec.Label,
                        target.PublicIngress.PublicBaseDomain,
                        target.PublicIngress.WorkloadConfigurationRevision),
                    session,
                    ct);
                return created switch
                {
                    TenantDomainZoneSnapshot => new GitOperationResult(operation.ResourceId, Changed: true),
                    ManagedDomainZoneNameConflict => new GitOperationResult(
                        operation.ResourceId,
                        new DeclarationPreconditionsChanged()),
                    ManagedDomainZoneAlreadyReserved => new GitOperationResult(
                        operation.ResourceId,
                        new DomainZoneDeclarationAlreadyReserved()),
                    ManagedDomainZoneDeploymentTargetChanged => new GitOperationResult(
                        operation.ResourceId,
                        new DeclarationPreconditionsChanged()),
                    ManagedDomainZoneReferenceRejected => new GitOperationResult(
                        operation.ResourceId,
                        new DomainZoneDeclarationTargetUnavailable()),
                };
            },
            cancellationToken);

    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Operation is { } operation
            ? await zones.Find(session, DomainFilters.ZoneById(request.TenantId, operation.ResourceId))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        current ??= await zones.Find(session, DomainFilters.ZoneByTechnicalName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();

        if (current.Identity is not NamedDomainIdentity identity || identity.TechnicalName != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        return new CurrentDeclaration(
            current.Id,
            new DeclarationVersionToken(
            [
                new DeclarationDocumentVersion(DomainLifecycleVersions.ZoneRevisionKey(current.Id), current.Revision),
                new DeclarationDocumentVersion(
                    DomainLifecycleVersions.ZoneReferenceFenceKey(current.Id),
                    current.ReferenceFence),
            ]),
            Project(current.DeploymentTargetId, current.Label),
            DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var target = await ReadTargetAsync(
            request.TenantId,
            request.Declaration,
            request.Operation,
            session,
            cancellationToken);
        if (target is null)
            return new DeclarationReadBlocked(new DomainZoneDeclarationTargetUnavailable());

        var spec = DeclarationJson.Read<DomainZoneDeclaration>(request.Declaration.Spec);
        return Project(target.PublicIngress.DeploymentTargetId, spec.Label) with
        {
            Dependencies = new DeclarationVersionToken(TargetVersions(target)),
        };
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var operation = request.Operation;
        var target = await ReadTargetAsync(
            operation.Context.TenantId,
            operation.Declaration,
            operation,
            session,
            cancellationToken);
        if (target is null || !TargetVersions(target).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();

        var current = await zones
            .Find(session, DomainFilters.ZoneById(operation.Context.TenantId, operation.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        var expected = request.Expected.Documents.SingleOrDefault(item =>
            item.DocumentId == DomainLifecycleVersions.ZoneRevisionKey(operation.ResourceId));
        var expectedFence = request.Expected.Documents.SingleOrDefault(item =>
            item.DocumentId == DomainLifecycleVersions.ZoneReferenceFenceKey(operation.ResourceId));
        if (current is null
                ? expected is not null || expectedFence is not null
                : expected?.Revision != current.Revision || expectedFence?.Revision != current.ReferenceFence)
            return new DeclarationPreconditionsChanged();

        if (await fenceTarget(target, session, cancellationToken) is not DomainReservationTargetPlanningFenced)
            return new DeclarationPreconditionsChanged();

        if (current is not null && await fenceZone(
                new FenceDomainZoneRequest(current.TenantId, current.Id, current.Revision, current.ReferenceFence),
                session,
                cancellationToken) is not DomainZoneFenced)
            return new DeclarationPreconditionsChanged();

        return new DeclarationFenced();
    }

    private async Task<DomainReservationTargetPlanningSnapshot?> ReadTargetAsync(
        string tenantId,
        CompiledDeclaration declaration,
        GitOperationInput? operation,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<DomainZoneDeclaration>(declaration.Spec);
        string? targetId;
        if (operation is not null)
        {
            targetId = await git.ResolveAcceptedAsync(
                new ResolveAcceptedGitReferenceRequest(
                    operation.Context,
                    operation.ResourceId,
                    new ResourceAddress("DeploymentTarget", spec.DeploymentTarget)),
                session,
                cancellationToken);
        }
        else
        {
            var found = await resolveTargetName(
                new NamedDomainReservationTargetRequest(tenantId, spec.DeploymentTarget),
                cancellationToken);
            targetId = found is DeploymentTargetLookupDto target ? target.Id : null;
        }

        if (targetId is null)
            return null;

        return await readTarget(
            new DomainReservationTargetPlanningRequest(tenantId, targetId),
            session,
            cancellationToken) is DomainReservationTargetPlanningSnapshot snapshot
            ? snapshot
            : null;
    }

    private static DeclarationProjection Project(string targetId, string label) => new(
        DeclarationJson.Write(new { deploymentTargetId = targetId, label }),
        DeclarationJson.Write(new { }));

    private static DeclarationDocumentVersion[] TargetVersions(DomainReservationTargetPlanningSnapshot target) =>
    [
        new(DeploymentTargetVersionKeys.Revision(target.PublicIngress.DeploymentTargetId), target.TargetRevision),
        new(
            DeploymentTargetVersionKeys.WorkloadConfiguration(target.PublicIngress.DeploymentTargetId),
            target.PublicIngress.WorkloadConfigurationRevision),
    ];

    private sealed record AcceptedTarget(
        string? Id
    );
}
