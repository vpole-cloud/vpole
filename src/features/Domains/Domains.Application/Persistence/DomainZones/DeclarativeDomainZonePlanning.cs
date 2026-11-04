using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Domains.Application.Persistence.DomainZones;

internal sealed partial class DeclarativeDomainZoneProvider
{
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

        return new CurrentDeclaration(current.Id,
            new([new($"DomainZone/{current.Id}", current.Revision),
                new($"DomainZone/{current.Id}/ReferenceFence", current.ReferenceFence)]),
            Project(current.DeploymentTargetId, current.Label), DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var target = await ReadTargetAsync(request.TenantId, request.Declaration, request.Operation,
            session, cancellationToken);
        if (target is null)
            return new DeclarationReadBlocked(new DomainZoneDeclarationTargetUnavailable());

        var spec = DeclarationJson.Read<DomainZoneDeclaration>(request.Declaration.Spec);
        return Project(target.PublicIngress.DeploymentTargetId, spec.Label) with
        {
            Dependencies = new(TargetVersions(target))
        };
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var operation = request.Operation;
        var target = await ReadTargetAsync(operation.Context.TenantId, operation.Declaration,
            operation, session, cancellationToken);
        if (target is null || !TargetVersions(target).All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();

        var current = await zones.Find(session,
                DomainFilters.ZoneById(operation.Context.TenantId, operation.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        var expected = request.Expected.Documents.SingleOrDefault(item =>
            item.DocumentId == $"DomainZone/{operation.ResourceId}");
        var expectedFence = request.Expected.Documents.SingleOrDefault(item =>
            item.DocumentId == $"DomainZone/{operation.ResourceId}/ReferenceFence");
        if (current is null ? expected is not null || expectedFence is not null :
            expected?.Revision != current.Revision || expectedFence?.Revision != current.ReferenceFence)
            return new DeclarationPreconditionsChanged();

        if (await fenceTarget(target, session, cancellationToken) is not DomainReservationTargetPlanningFenced)
            return new DeclarationPreconditionsChanged();

        if (current is not null && await fenceZone(new(current.TenantId, current.Id,
                current.Revision, current.ReferenceFence), session, cancellationToken) is not DomainZoneFenced)
            return new DeclarationPreconditionsChanged();

        return new DeclarationFenced();
    }
}
