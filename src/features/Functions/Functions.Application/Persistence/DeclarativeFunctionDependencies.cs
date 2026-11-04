using MongoDB.Driver;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceDeployments.Contracts;

namespace Pole.Features.Functions.Application.Persistence;

internal sealed partial class DeclarativeFunctionProvider
{
    public string DependencyOwner => "Functions";

    public async Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
        ReadLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var result = new List<GitLifecycleDependency>();
        using var cursor = await resources.Find(session, FunctionAppFilters.ByTenant(request.TenantId))
            .ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var root in cursor.Current)
            {
                var state = await ReadLifecycleStateAsync(root, session, cancellationToken);
                var references = request.Resource.Address.Kind switch
                {
                    "ResourceGroup" => root.ResourceGroupId == request.Resource.ResourceId,
                    "IsolationScope" => state.Placements.Any(item =>
                        item.IsolationScopeId == request.Resource.ResourceId),
                    "Secret" => state.Configuration.SelectMany(item => item.Spec.Configuration.SecretReferences.Values)
                        .Any(item => item.SecretId == request.Resource.ResourceId),
                    _ => false
                };
                if (references)
                    result.Add(new(new(new(Kind, root.NormalizedName), root.Id), request.Resource,
                        new(LifecycleVersions(state))));
            }
        }

        return result.OrderBy(item => item.Dependent.ResourceId, StringComparer.Ordinal).ToArray();
    }

    public async Task<FenceDeclarationOutcome> FenceDependenciesAsync(
        FenceLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = await ReadDependenciesAsync(new(request.TenantId, request.Resource), session, cancellationToken);
        if (current.Count != request.Expected.Count || current.Any(item => !request.Expected.Any(expected =>
                expected.Dependent == item.Dependent && expected.Target == item.Target &&
                SameLifecycleVersions(expected.Version.Documents, item.Version.Documents))))
            return new DeclarationPreconditionsChanged();

        foreach (var dependency in current)
        {
            var root = await resources.Find(session,
                FunctionAppFilters.ByTenantAndId(request.TenantId, dependency.Dependent.ResourceId))
                .FirstOrDefaultAsync(cancellationToken);
            if (root is null)
                return new DeclarationPreconditionsChanged();
            var state = await ReadLifecycleStateAsync(root, session, cancellationToken);
            var fenced = await resources.UpdateOneAsync(session, FunctionAppFilters.AtPlanningVersion(root),
                Builders<FunctionApp>.Update.Inc(item => item.PlanningFence, 1), cancellationToken: cancellationToken);
            if (fenced.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
            if (state.Placements.Count > 0 && await fencePlacements(
                    new(root.TenantId, ResourceType, root.Id, state.Placements), session, cancellationToken)
                is not ResourceDeploymentPlanningFenced)
                return new DeclarationPreconditionsChanged();

            if (state.Route is { } route)
            {
                var routeFenced = await routes.UpdateOneAsync(session,
                    FunctionRouteBindingFilters.AtPlanningVersion(route),
                    Builders<FunctionRouteBinding>.Update.Inc(item => item.PlanningFence, 1),
                    cancellationToken: cancellationToken);
                if (routeFenced.ModifiedCount != 1)
                    return new DeclarationPreconditionsChanged();
            }
            foreach (var revision in state.Configuration)
            {
                var changed = await revisions.UpdateOneAsync(session,
                    FunctionRevisionFilters.AtPlanningVersion(revision),
                    Builders<FunctionRevision>.Update.Inc(item => item.PlanningFence, 1),
                    cancellationToken: cancellationToken);
                if (changed.ModifiedCount != 1)
                    return new DeclarationPreconditionsChanged();
            }
        }

        return new DeclarationFenced();
    }
}
