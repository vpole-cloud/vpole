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

namespace Pole.Features.Domains.Application.Persistence.HostnameBindings;

public sealed record HostnameBindingDeclaration(
    string Zone,
    ResourceAddress Resource,
    string DeploymentTarget,
    string RelativeName);

public sealed record HostnameDeclarationDependencyUnavailable : IDeclarationFailure;

public sealed record HostnameDeclarationAlreadyReserved : IDeclarationFailure;

internal sealed partial class DeclarativeHostnameBindingProvider(
    IMongoDatabase database,
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
    private readonly IMongoCollection<TenantDomainZone> zones =
        database.GetCollection<TenantDomainZone>(DomainCollectionNames.TenantDomainZones);

    private readonly IMongoCollection<HostnameBinding> bindings =
        database.GetCollection<HostnameBinding>(DomainCollectionNames.HostnameBindings);

    private readonly IReadOnlyDictionary<string, IHostnameBindingWorkloadCapability> capabilities =
        workloadCapabilities.ToDictionary(item => item.Kind, StringComparer.Ordinal);

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
        DeclarationJson.Require(ResourceTechnicalName.IsValid(declaration.Zone), "spec.zone");
        DeclarationJson.Require(ResourceTechnicalName.IsValid(declaration.DeploymentTarget), "spec.deploymentTarget");
        DeclarationJson.Require(declaration.Resource is not null &&
            DeclarationNames.Valid(declaration.Resource.Name) &&
            !string.IsNullOrWhiteSpace(declaration.Resource.Kind) &&
            capabilities.ContainsKey(declaration.Resource.Kind), "spec.resource");
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
        return new(null, [], [new(new("DomainZone", declaration.Zone), "spec.zone"),
            new(declaration.Resource, "spec.resource")], [], [], ReferencesShareScope: false);
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
}
