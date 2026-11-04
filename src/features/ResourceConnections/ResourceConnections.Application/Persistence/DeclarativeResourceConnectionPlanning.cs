using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Application.Persistence;

internal sealed partial class DeclarativeResourceConnectionProvider
{
    public async Task<ReadCurrentDeclarationOutcome> ReadCurrentAsync(
        ReadCurrentDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Operation is { } operation
            ? await connections.Find(session, ResourceConnectionFilters.ByTenantAndId(request.TenantId,
                operation.ResourceId)).FirstOrDefaultAsync(cancellationToken)
            : null;
        current ??= await connections.Find(session,
                ResourceConnectionFilters.ByName(request.TenantId, request.Address.Name))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new CurrentDeclarationMissing();

        if (current.Name != request.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        if (current.Lifecycle is not ActiveResourceConnection)
            return new DeclarationReadBlocked(new ConnectionDeclarationUnavailable());

        return new CurrentDeclaration(current.Id, new([new($"ResourceConnection/{current.Id}", current.Revision)]),
            Project(new(current.TenantId, current.Name, ResourceTypeId.Create(current.SourceResourceTypeId),
                current.SourceResourceId, ResourceTypeId.Create(current.DestinationResourceTypeId),
                current.DestinationResourceId, current.EndpointId)), DeclarationConfigurationState.Configured);
    }

    public async Task<ProjectDesiredDeclarationOutcome> ProjectDesiredAsync(
        ProjectDesiredDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveDeclarationAsync(request.TenantId, request.Declaration, request.Operation,
            session, cancellationToken);
        return resolved is null
            ? new DeclarationReadBlocked(new ConnectionDeclarationUnavailable())
            : await ProjectResolvedAsync(resolved, session, cancellationToken);
    }

    private async Task<ProjectDesiredDeclarationOutcome> ProjectResolvedAsync(
        ResolvedDeclaration resolved,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var topology = await admission.ReadAsync(resolved.Command, session, cancellationToken);
        return topology switch
        {
            ResolvedResourceConnectionTopology => Project(resolved.Command) with
            { Dependencies = new(resolved.Versions) },
            ConnectionResourceMissing => new DeclarationReadBlocked(new ConnectionDeclarationUnavailable()),
            ConnectionPlacementDeleting => new DeclarationReadBlocked(new ConnectionDeclarationUnavailable()),
            ConnectionEndpointUnsupported => new DeclarationReadBlocked(new ConnectionDeclarationEndpointUnavailable()),
            ConnectionSourceHasNoPlacements =>
                new DeclarationReadBlocked(new ConnectionDeclarationTopologyUnavailable()),
            ConnectionDestinationTargetMissing =>
                new DeclarationReadBlocked(new ConnectionDeclarationTopologyUnavailable()),
            ConnectionDestinationTargetAmbiguous =>
                new DeclarationReadBlocked(new ConnectionDeclarationTopologyUnavailable())
        };
    }

    public async Task<FenceDeclarationOutcome> FenceAsync(
        FenceDeclarationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var operation = request.Operation;
        var resolved = await ResolveDeclarationAsync(operation.Context.TenantId, operation.Declaration, operation,
            session, cancellationToken);
        if (resolved is null || !resolved.Versions.All(request.Expected.Documents.Contains))
            return new DeclarationPreconditionsChanged();

        var expected = request.Expected.Documents.SingleOrDefault(item =>
            item.DocumentId == $"ResourceConnection/{operation.ResourceId}");
        var current = await connections.Find(session,
            ResourceConnectionFilters.ByTenantAndId(operation.Context.TenantId, operation.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null ? expected is not null : expected?.Revision != current.Revision ||
            current.Lifecycle is not ActiveResourceConnection)
            return new DeclarationPreconditionsChanged();

        foreach (var registration in resolved.Registrations)
        {
            if (await fenceRegistration(registration, session, cancellationToken)
                is not ResourceRegistrationSnapshotFenced)
                return new DeclarationPreconditionsChanged();
        }

        if (current is not null)
        {
            var fenced = await connections.UpdateOneAsync(session, ResourceConnectionFilters.AtRevision(
                current.TenantId, current.Id, current.Revision),
                Builders<ResourceConnection>.Update.Inc(item => item.ReferenceFence, 1),
                cancellationToken: cancellationToken);
            if (fenced.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }
}
