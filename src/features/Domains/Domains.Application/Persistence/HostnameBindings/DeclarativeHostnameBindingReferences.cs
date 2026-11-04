using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Dns;

namespace Pole.Features.Domains.Application.Persistence.HostnameBindings;

internal sealed partial class DeclarativeHostnameBindingProvider
{
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
