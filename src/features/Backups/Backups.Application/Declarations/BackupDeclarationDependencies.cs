using MongoDB.Driver;
using Pole.Features.Backups.Application.Mongo;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Workloads;

namespace Pole.Features.Backups.Application.Declarations;

internal sealed class BackupDeclarationDependencies(
    IMongoCollection<BackupPolicy> policies,
    IMongoCollection<BackupDestination> destinations,
    IEnumerable<IBackupProvider> services,
    LoadResourceRegistrationPlanningSnapshot loadRegistration,
    FenceResourceRegistrationPlanningSnapshot fenceRegistration
) : IDeclarativeLifecycleDependencyProvider
{
    private readonly IReadOnlyDictionary<string, IBackupProvider> providers = services.ToDictionary(
        p => p.DeclarationKind,
        StringComparer.Ordinal);

    public string DependencyOwner => "Backups";

    public async Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
        ReadLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        FilterDefinition<BackupPolicy> filter;
        if (request.Resource.Address.Kind == DeclarativeBackupDestinationProvider.DeclarationKind)
            filter = BackupsPolicyFilters.ForDestination(request.TenantId, request.Resource.ResourceId);
        else if (providers.TryGetValue(request.Resource.Address.Kind, out var service))
            filter = BackupsPolicyFilters.ForSource(
                request.TenantId,
                service.ResourceType,
                request.Resource.ResourceId);
        else
            return [];
        var current = await policies.Find(session, filter).ToListAsync(ct);
        return current.OrderBy(p => p.Id, StringComparer.Ordinal)
            .Select(p => new GitLifecycleDependency(
                new(new(DeclarativeBackupPolicyProvider.DeclarationKind, p.Name), p.Id),
                request.Resource,
                new(
                [
                    new(
                        DeclarationVersionKeys.Resource(DeclarativeBackupPolicyProvider.DeclarationKind, p.Id),
                        p.Revision)
                ])))
            .ToArray();
    }

    public async Task<FenceDeclarationOutcome> FenceDependenciesAsync(
        FenceLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var actual = await ReadDependenciesAsync(new(request.TenantId, request.Resource), session, ct);
        if (actual.Count != request.Expected.Count || actual.Any(item => !request.Expected.Any(expect =>
                expect.Target == item.Target && expect.Dependent == item.Dependent &&
                expect.Version.Documents.SequenceEqual(item.Version.Documents))))
            return new DeclarationPreconditionsChanged();
        if (request.Resource.Address.Kind == DeclarativeBackupDestinationProvider.DeclarationKind)
        {
            var result = await destinations.UpdateOneAsync(
                session,
                BackupsFilters<BackupDestination>.ById(request.TenantId, request.Resource.ResourceId),
                Builders<BackupDestination>.Update.Inc(d => d.ReferenceFence, 1),
                cancellationToken: ct);
            if (result.MatchedCount == 0 && actual.Count > 0)
                return new DeclarationPreconditionsChanged();
        }
        else if (providers.TryGetValue(request.Resource.Address.Kind, out var provider))
        {
            var registration = await loadRegistration(
                new(request.TenantId, ResourceTypeId.Create(provider.ResourceType), request.Resource.ResourceId),
                session,
                ct);
            if (registration is null
                    ? actual.Count > 0
                    : await fenceRegistration(registration, session, ct) is not ResourceRegistrationSnapshotFenced)
                return new DeclarationPreconditionsChanged();
        }

        foreach (var item in actual)
        {
            var write = await policies.UpdateOneAsync(
                session,
                BackupConfigurationFilters<BackupPolicy>.AtRevision(
                    request.TenantId,
                    item.Dependent.ResourceId,
                    item.Version.Documents.Single().Revision),
                Builders<BackupPolicy>.Update.Inc(p => p.PlanningFence, 1),
                cancellationToken: ct);
            if (write.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }
}
