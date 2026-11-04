using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Persistence;

internal sealed partial class DeclarativeWebServiceProvider
{
    public string DependencyOwner => "WebServices";

    public async Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
        ReadLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var result = new List<GitLifecycleDependency>();
        using var cursor = await services.Find(session, WebServiceFilters.VisibleToTenant(request.TenantId))
            .ToCursorAsync(cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var current in cursor.Current)
            {
                if (References(current, request.Resource))
                    result.Add(new(new(new(Kind, current.Name), current.Id), request.Resource,
                        new(LifecycleVersions(current, [])), request.Resource.Address.Kind switch
                        {
                            "SourceBuildConfiguration" => GitLifecycleDependencyOrder.AfterDependentRemovalRequested,
                            "ResourceConnection" => GitLifecycleDependencyOrder.AfterRemovalRequested,
                            _ => GitLifecycleDependencyOrder.BeforeRemoval
                        }));
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
                expected.Order == item.Order &&
                SameLifecycleVersions(expected.Version.Documents, item.Version.Documents))))
            return new DeclarationPreconditionsChanged();

        foreach (var dependency in current)
        {
            var root = await services.Find(session,
                WebServiceFilters.ById(request.TenantId, dependency.Dependent.ResourceId))
                .FirstOrDefaultAsync(cancellationToken);
            if (root is null)
                return new DeclarationPreconditionsChanged();
            var fenced = await services.UpdateOneAsync(session, WebServiceFilters.AtPlanningVersion(root),
                Builders<WebService>.Update.Inc(item => item.PlanningFence, 1),
                cancellationToken: cancellationToken);
            if (fenced.ModifiedCount != 1)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }

    private static bool References(WebService current, GitLifecycleResourceReference target) =>
        target.Address.Kind switch
        {
            "ResourceGroup" => current.ResourceGroupId == target.ResourceId,
            "IsolationScope" => current.IsolationScopeId == target.ResourceId,
            "Secret" => current.Spec.Config.SecretEnvironmentReferences.Values
                .Any(item => item.SecretId == target.ResourceId),
            "ResourceConnection" => current.Spec.Config.ConnectionEnvironmentReferences.Values
                .Any(item => item.ConnectionId == target.ResourceId),
            "SourceBuildConfiguration" => current.Lifecycle is not DeletingWebServiceLifecycle &&
                current.Spec.Image.SourceBuildConfigurationId == target.ResourceId,
            _ => false
        };
}
