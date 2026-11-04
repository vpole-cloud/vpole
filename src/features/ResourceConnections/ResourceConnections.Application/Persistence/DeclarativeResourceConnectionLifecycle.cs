using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceConnections.Domain;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.ResourceConnections.Application.Persistence;

internal sealed partial class DeclarativeResourceConnectionProvider
{
    public string DependencyOwner => "ResourceConnections";

    public async Task<ReadLifecycleResourceOutcome> ReadLifecycleAsync(
        ReadLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await connections.Find(session,
                ResourceConnectionFilters.ByTenantAndId(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null)
            return new GitLifecycleResourceAbsent();

        if (request.Resource.Address.Kind != Kind || current.Name != request.Resource.Address.Name)
            return new DeclarationReadBlocked(new DeclarationIdentityChanged());

        return new GitLifecycleResourceSnapshot(request.Resource, LifecycleVersion(current),
            current.Lifecycle is ActiveResourceConnection ? GitLifecyclePresence.Active : GitLifecyclePresence.Deleting,
            GitLifecycleProtection.ConfigurationOnly, GitLifecycleVersionSemantics.ObservedDocuments);
    }

    public async Task<RemoveLifecycleResourceOutcome> RemoveAsync(
        RemoveLifecycleResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var read = await ReadLifecycleAsync(new(request.Authority.TenantId, request.Resource),
            session, cancellationToken);
        if (read is GitLifecycleResourceAbsent)
            return new GitLifecycleResourceRemoved();
        if (read is DeclarationReadBlocked blocked)
            return blocked;

        var current = (GitLifecycleResourceSnapshot)read.Value;
        if (!SameVersions(current.Version, request.Expected))
            return new DeclarationPreconditionsChanged();

        var expected = ResourceConnectionPlanningVersion.FromDocuments(request.Resource.ResourceId,
            current.Version.Documents.ToDictionary(item => item.DocumentId, item => item.Revision))!;
        var outcome = await deletion.ExecuteAsync(new(request.Authority.TenantId, request.Resource.ResourceId,
            expected, request.Authority), session, cancellationToken);
        return outcome switch
        {
            ResourceConnection => new GitLifecycleRemovalRequested(),
            ResourceConnectionMissing => new GitLifecycleResourceRemoved(),
            ResourceConnectionDeletionConflict => new DeclarationPreconditionsChanged(),
            ResourceConnectionGitOwned => new StaleGitExecution()
        };
    }

    public async Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
        ReadLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!kinds.TryGetValue(request.Resource.Address.Kind, out var resourceType))
            return [];

        var affected = await connections.Find(session,
                ResourceConnectionFilters.ByResource(request.TenantId, resourceType, request.Resource.ResourceId))
            .SortBy(item => item.Id).ToListAsync(cancellationToken);
        return affected.Select(item => new GitLifecycleDependency(
            new(new(Kind, item.Name), item.Id), request.Resource, LifecycleVersion(item),
            GitLifecycleDependencyOrder.AfterDependentRemovalRequested)).ToArray();
    }

    public async Task<FenceDeclarationOutcome> FenceDependenciesAsync(
        FenceLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Connection dependency fencing requires a transaction.");

        var current = await ReadDependenciesAsync(new(request.TenantId, request.Resource), session, cancellationToken);
        if (current.Count != request.Expected.Count || current.Any(item =>
                !request.Expected.Any(expected => expected.Dependent == item.Dependent &&
                    expected.Target == item.Target && expected.Order == item.Order &&
                    SameVersions(expected.Version, item.Version))))
            return new DeclarationPreconditionsChanged();

        if (!kinds.TryGetValue(request.Resource.Address.Kind, out var resourceType))
            return new DeclarationFenced();

        // Connection admission writes this same registration. This closes the phantom-reference race
        // even when the reviewed dependency set was empty.
        var registration = await loadRegistration(new(request.TenantId, resourceType, request.Resource.ResourceId),
            session, cancellationToken);
        if (registration is not null && await fenceRegistration(registration, session, cancellationToken)
            is not ResourceRegistrationSnapshotFenced)
            return new DeclarationPreconditionsChanged();

        foreach (var dependency in current)
        {
            var connection = await connections.Find(session,
                ResourceConnectionFilters.ByTenantAndId(request.TenantId, dependency.Dependent.ResourceId))
                .SingleAsync(cancellationToken);
            if (!SameVersions(LifecycleVersion(connection), dependency.Version))
                return new DeclarationPreconditionsChanged();
            // Deleting configuration is frozen. The parent's mandatory joined reviewed cascade
            // conditionally writes this exact live token after authorization in the same transaction.
            // Advancing it here would invalidate the approved cascade's own preconditions.
            if (connection.Lifecycle is DeletingResourceConnection)
                continue;

            var revision = dependency.Version.Documents[0].Revision;
            var fence = dependency.Version.Documents[1].Revision;
            var result = await connections.UpdateOneAsync(session,
                ResourceConnectionFilters.AtPlanningVersion(request.TenantId,
                    new(dependency.Dependent.ResourceId, revision, fence)),
                Builders<ResourceConnection>.Update.Inc(item => item.ReferenceFence, 1),
                cancellationToken: cancellationToken);
            if (result.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }

    private static DeclarationVersionToken LifecycleVersion(ResourceConnection current) => new(
        [new(ResourceConnectionPlanningVersion.RevisionDocumentId(current.Id), current.Revision),
            new(ResourceConnectionPlanningVersion.ReferenceFenceDocumentId(current.Id), current.ReferenceFence)]);

    private static bool SameVersions(DeclarationVersionToken left, DeclarationVersionToken right) =>
        left.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .SequenceEqual(right.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal));
}
