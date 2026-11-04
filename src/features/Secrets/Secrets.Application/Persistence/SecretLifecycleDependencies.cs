using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

internal sealed class SecretLifecycleDependencies(
    IMongoCollection<Secret> secrets,
    IEnumerable<SecretDeclarationConsumer> consumers,
    SecretPlanningFence secretFence,
    LoadResourceRegistrationPlanningSnapshot loadRegistration,
    LoadIsolationScopePlanningSnapshot loadScope,
    FenceIsolationScopePlanningSnapshot fenceScope,
    LoadResourceDeploymentPlanningSnapshot loadPlacements,
    FenceResourceDeploymentPlanningSnapshot fencePlacements) : IDeclarativeLifecycleDependencyProvider
{
    private readonly IReadOnlyDictionary<string, SecretDeclarationConsumer> kinds =
        consumers.ToDictionary(item => item.Kind, StringComparer.Ordinal);

    public string DependencyOwner => "Secrets";

    public async Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
        ReadLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var filter = SecretLifecycleFilters.Selection(request, kinds);
        if (filter is null)
            return [];

        var current = await secrets.Find(session, filter).SortBy(item => item.Id).ToListAsync(ct);
        return current.SelectMany(secret => Dependents(request, secret).Select(resource => new GitLifecycleDependency(
            resource, request.Resource, new(SecretPlanningFence.Versions(secret))))).ToArray();
    }

    public async Task<FenceDeclarationOutcome> FenceDependenciesAsync(
        FenceLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Secret dependency fencing requires a transaction.");

        var current = await ReadDependenciesAsync(new(request.TenantId, request.Resource), session, ct);
        if (current.Count != request.Expected.Count || current.Any(item => !request.Expected.Any(expected =>
                expected.Dependent == item.Dependent && expected.Target == item.Target &&
                SecretLifecycleVersions.Same(item.Version, expected.Version))))
            return new DeclarationPreconditionsChanged();

        var selection = SecretLifecycleFilters.Selection(new(request.TenantId, request.Resource), kinds);
        if (selection is not null)
        {
            var roots = await secrets.Find(session, selection).ToListAsync(ct);
            foreach (var root in roots)
                if (!await secretFence.FenceAsync(root, session, ct))
                    return new DeclarationPreconditionsChanged();
        }

        if (kinds.TryGetValue(request.Resource.Address.Kind, out var consumer))
        {
            // Binding admission writes the consumer's group and placements, even for a previously empty set.
            var registration = await loadRegistration(new(request.TenantId, consumer.ResourceType,
                request.Resource.ResourceId), session, ct);
            if (registration is not null)
            {
                var scope = await loadScope(new(request.TenantId, registration.Resource.ResourceGroupId, null),
                    session, ct);
                if (scope is not IsolationScopePlanningSnapshot snapshot ||
                    await fenceScope(snapshot, session, ct) is not IsolationScopePlanningFenced)
                    return new DeclarationPreconditionsChanged();
            }

            var placements = await loadPlacements(new(request.TenantId, consumer.ResourceType,
                request.Resource.ResourceId), session, ct);
            if (placements is ResourceDeploymentPlanningSnapshot placed &&
                await fencePlacements(placed, session, ct) is not ResourceDeploymentPlanningFenced)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }

    private IEnumerable<GitLifecycleResourceReference> Dependents(ReadLifecycleDependenciesRequest request,
        Secret secret)
    {
        if (request.Resource.Address.Kind is "ResourceGroup" or "IsolationScope")
        {
            if (secret.Management is UnmanagedSecret)
                yield return new(new("Secret", secret.Name), secret.Id);
            yield break;
        }

        foreach (var binding in secret.Bindings)
        {
            if (secret.Management is ProviderManagedSecret owner && owner.BindingId == binding.Id)
                continue;
            if (request.Resource.Address.Kind != "Secret" &&
                (!kinds.TryGetValue(request.Resource.Address.Kind, out var consumer) ||
                    binding.Consumer.ResourceType != consumer.ResourceType.Value ||
                    binding.Consumer.ResourceId != request.Resource.ResourceId))
                continue;

            yield return new(new("SecretBinding", binding.DeclarationName ?? binding.Id), binding.Id);
        }
    }
}

internal static class SecretLifecycleFilters
{
    public static FilterDefinition<Secret>? Selection(ReadLifecycleDependenciesRequest request,
        IReadOnlyDictionary<string, SecretDeclarationConsumer> kinds)
    {
        var tenant = Builders<Secret>.Filter.Eq(item => item.TenantId, request.TenantId);
        var id = request.Resource.ResourceId;
        if (request.Resource.Address.Kind == "Secret")
            return tenant & Builders<Secret>.Filter.Eq(item => item.Id, id);
        if (request.Resource.Address.Kind == "ResourceGroup")
            return tenant & Builders<Secret>.Filter.Eq(item => item.ResourceGroupId, id);
        if (request.Resource.Address.Kind == "IsolationScope")
            return tenant & Builders<Secret>.Filter.Eq(item => item.IsolationScopeId, id);
        return kinds.TryGetValue(request.Resource.Address.Kind, out var consumer)
            ? tenant & Builders<Secret>.Filter.ElemMatch(item => item.Bindings,
                binding => binding.Consumer.ResourceType == consumer.ResourceType.Value &&
                    binding.Consumer.ResourceId == id)
            : null;
    }

}
