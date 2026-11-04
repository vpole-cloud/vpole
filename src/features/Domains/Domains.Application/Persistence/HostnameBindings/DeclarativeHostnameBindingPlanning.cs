using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Domains.Application.Persistence.HostnameBindings;

internal sealed partial class DeclarativeHostnameBindingProvider
{
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
            item.DocumentId == $"HostnameBinding/{operation.ResourceId}");
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
        new($"HostnameBinding/{binding.Id}", binding.ReferenceFence);

    private static DeclarationDocumentVersion[] Versions(ResolvedHostname context) =>
    [
        new($"DomainZone/{context.Zone.Id}", context.Zone.Revision),
        new($"DomainZone/{context.Zone.Id}/ReferenceFence", context.Zone.ReferenceFence),
        .. TargetVersions(context.Target),
        .. context.Workload.DocumentVersions.Select(item =>
            new DeclarationDocumentVersion(item.DocumentId, item.Version))
    ];

    private static DeclarationDocumentVersion[] TargetVersions(DomainReservationTargetPlanningSnapshot target) =>
    [
        new($"DeploymentTarget/{target.PublicIngress.DeploymentTargetId}", target.TargetRevision),
        new($"DeploymentTarget/{target.PublicIngress.DeploymentTargetId}/WorkloadConfiguration",
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
}
