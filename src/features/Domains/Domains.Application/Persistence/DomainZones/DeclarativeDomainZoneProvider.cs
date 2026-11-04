using System.Text.Json;
using MongoDB.Driver;
using Pole.Database;
using Pole.Dns;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Workloads;

namespace Pole.Features.Domains.Application.Persistence.DomainZones;

public sealed record DomainZoneDeclaration(
    string DeploymentTarget,
    string Label);

public sealed record DomainZoneDeclarationTargetUnavailable : IDeclarationFailure;

public sealed record DomainZoneDeclarationTargetAmbiguous : IDeclarationFailure;

public sealed record DomainZoneDeclarationAlreadyReserved : IDeclarationFailure;

internal sealed partial class DeclarativeDomainZoneProvider(
    IMongoDatabase database,
    IGitOpsMutations git,
    MongoRunTransaction transactions,
    ResolveNamedDomainReservationTarget resolveTargetName,
    ResolveDomainReservationTargetPlanningSnapshot readTarget,
    FenceDomainReservationTargetPlanningSnapshot fenceTarget,
    CreateManagedDomainZoneReservation createZone,
    FenceDomainZone fenceZone,
    MongoDeleteManagedDomainZone deletion) : ITransactionalDeclarativeResourceProvider,
        IDeclarativeReferencePreparation,
        IDeclarativeResourceLifecycleProvider, IDeclarativeAdoptionPreviewProvider, IDeclarativePreviewReferenceProvider
{
    private readonly IMongoCollection<TenantDomainZone> zones =
        database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);

    public string Kind => "DomainZone";

    public string DescribeFailure(IDeclarationFailure failure) => failure switch
    {
        DomainZoneDeclarationTargetUnavailable => nameof(DomainZoneDeclarationTargetUnavailable),
        DomainZoneDeclarationTargetAmbiguous => nameof(DomainZoneDeclarationTargetAmbiguous),
        DomainZoneDeclarationAlreadyReserved => nameof(DomainZoneDeclarationAlreadyReserved),
        _ => throw new ArgumentException("The failure belongs to another declaration provider.", nameof(failure))
    };

    public JsonElement NormalizeAndValidate(JsonElement spec)
    {
        var declaration = DeclarationJson.Read<DomainZoneDeclaration>(spec);
        DeclarationJson.Require(ResourceTechnicalName.IsValid(declaration.DeploymentTarget), "spec.deploymentTarget");
        DeclarationJson.Require(DnsLabel.IsValid(declaration.Label) &&
            TenantDomainZoneLabelPolicy.IsManagedLabelAllowed(declaration.Label), "spec.label");
        return DeclarationJson.Write(declaration with { Label = DnsLabel.Create(declaration.Label).Value });
    }

    public IReadOnlyList<DeclarationStep> Steps(ResourceAddress address, JsonElement spec) =>
        [new(DeclarationPhase.Prepare, [])];

    public async Task<string?> ResolveAsync(
        ResolveDeclaredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) =>
        (await zones.Find(session, DomainFilters.ZoneByTechnicalName(request.TenantId, request.Name))
            .FirstOrDefaultAsync(cancellationToken))?.Id;

    public async Task<PrepareDeclarationReferencesOutcome> PrepareReferencesAsync(
        GitOperationInput operation,
        CancellationToken cancellationToken)
    {
        var spec = DeclarationJson.Read<DomainZoneDeclaration>(operation.Declaration.Spec);
        var address = new ResourceAddress("DeploymentTarget", spec.DeploymentTarget);
        var accepted = await transactions.InvokeOutcomeAsync<AcceptedTarget>(async (transaction, ct) =>
            transaction.Commit(new AcceptedTarget(await git.ResolveAcceptedAsync(
                new(operation.Context, operation.ResourceId, address), transaction.Session, ct))), cancellationToken);
        if (accepted.Id is not null)
            return new DeclarationReferencesPrepared();

        var found = await resolveTargetName(new(operation.Context.TenantId, address.Name), cancellationToken);
        if (found is not DeploymentTargetLookupDto target)
            return new DeclarationReadBlocked(found is NamedDeploymentTargetAmbiguous
                ? new DomainZoneDeclarationTargetAmbiguous()
                : new DomainZoneDeclarationTargetUnavailable());

        var pinned = await git.PinReferenceAsync(new(operation.Context, operation.ResourceId,
            new(address, target.Id)), cancellationToken);
        return pinned switch
        {
            GitReferencePinned => new DeclarationReferencesPrepared(),
            StaleGitExecution stale => stale
        };
    }

    public Task<GitOperationResult> ApplyAsync(GitOperation operation, CancellationToken cancellationToken) =>
        git.ExecuteAsync(operation, async (session, ct) =>
        {
            var target = await ReadTargetAsync(operation.Context.TenantId, operation.Declaration,
                operation.Input, session, ct);
            if (target is null)
                return new(operation.ResourceId, new DomainZoneDeclarationTargetUnavailable());

            var spec = DeclarationJson.Read<DomainZoneDeclaration>(operation.Declaration.Spec);
            var created = await createZone(new(operation.ResourceId, operation.Context.TenantId,
                target.PublicIngress.DeploymentTargetId, operation.Declaration.Address.Name, spec.Label,
                target.PublicIngress.PublicBaseDomain, target.PublicIngress.WorkloadConfigurationRevision),
                session, ct);
            return created switch
            {
                TenantDomainZoneSnapshot => new(operation.ResourceId, Changed: true),
                ManagedDomainZoneNameConflict => new(operation.ResourceId, new DeclarationPreconditionsChanged()),
                ManagedDomainZoneAlreadyReserved =>
                    new(operation.ResourceId, new DomainZoneDeclarationAlreadyReserved()),
                ManagedDomainZoneDeploymentTargetChanged =>
                    new(operation.ResourceId, new DeclarationPreconditionsChanged()),
                ManagedDomainZoneReferenceRejected =>
                    new(operation.ResourceId, new DomainZoneDeclarationTargetUnavailable())
            };
        }, cancellationToken);

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
            targetId = await git.ResolveAcceptedAsync(new(operation.Context, operation.ResourceId,
                new("DeploymentTarget", spec.DeploymentTarget)), session, cancellationToken);
        }
        else
        {
            var found = await resolveTargetName(new(tenantId, spec.DeploymentTarget), cancellationToken);
            targetId = found is DeploymentTargetLookupDto target ? target.Id : null;
        }

        if (targetId is null)
            return null;

        return await readTarget(new(tenantId, targetId), session, cancellationToken)
            is DomainReservationTargetPlanningSnapshot snapshot ? snapshot : null;
    }

    private static DeclarationProjection Project(string targetId, string label) =>
        new(DeclarationJson.Write(new { deploymentTargetId = targetId, label }), DeclarationJson.Write(new { }));

    private static DeclarationDocumentVersion[] TargetVersions(DomainReservationTargetPlanningSnapshot target) =>
    [
        new($"DeploymentTarget/{target.PublicIngress.DeploymentTargetId}", target.TargetRevision),
        new($"DeploymentTarget/{target.PublicIngress.DeploymentTargetId}/WorkloadConfiguration",
            target.PublicIngress.WorkloadConfigurationRevision)
    ];

    private sealed record AcceptedTarget(string? Id);
}
