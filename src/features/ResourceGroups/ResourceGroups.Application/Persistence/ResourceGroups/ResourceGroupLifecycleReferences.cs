using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceGroups.Application.Persistence.IsolationScopes;
using Pole.Features.ResourceGroups.Application.Persistence.ResourceRegistrations;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.Persistence.ResourceGroups;

internal sealed class ResourceGroupLifecycleReferences(
    IMongoCollection<ResourceGroup> groups,
    IMongoCollection<IsolationScope> scopes,
    IMongoCollection<ResourceRegistration> registrations,
    IServiceProvider services) : IDeclarativeLifecycleDependencyProvider
{
    private readonly Lazy<IReadOnlyDictionary<string, IDeclarativeResourceProvider>> registeredProviders = new(() =>
        services.GetServices<IDeclarativeResourceProvider>()
        .Where(provider => provider is IDeclarativeRegisteredResourceProvider)
        .ToDictionary(provider => ((IDeclarativeRegisteredResourceProvider)provider).ResourceType.Value,
            provider => provider, StringComparer.Ordinal));

    public string DependencyOwner => "ResourceGroups";

    public async Task<bool> HasKnownRegistrationsAsync(
        string tenantId,
        string groupId,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        return await ResolveRegistrationsAsync(tenantId, groupId, session, ct) is not null;
    }

    public async Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
        ReadLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (request.Resource.Address.Kind != "ResourceGroup")
            return [];

        var children = await scopes.Find(session,
            IsolationScopeFilters.ByTenantAndResourceGroup(request.TenantId, request.Resource.ResourceId))
            .ToListAsync(cancellationToken);
        var resources = await ResolveRegistrationsAsync(request.TenantId, request.Resource.ResourceId, session,
            cancellationToken);
        // The lifecycle read rejects protected ownership first, in the same snapshot transaction.
        if (resources is null)
            return [];

        return children.Where(item => !item.IsDefault).Select(item => new GitLifecycleDependency(
                new(new("IsolationScope", item.Name), item.Id), request.Resource,
                new([new($"IsolationScope/{item.Id}", item.Revision)])))
            .Concat(resources.Select(item => new GitLifecycleDependency(item.Dependent, request.Resource,
                item.Version)))
            .OrderBy(item => item.Dependent.Address.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Dependent.ResourceId, StringComparer.Ordinal).ToArray();
    }

    public async Task<FenceDeclarationOutcome> FenceDependenciesAsync(
        FenceLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Resource group dependency fencing requires a transaction.");
        if (request.Resource.Address.Kind != "ResourceGroup")
            return request.Expected.Count == 0 ? new DeclarationFenced() : new DeclarationPreconditionsChanged();
        if (!await HasKnownRegistrationsAsync(request.TenantId, request.Resource.ResourceId,
                session, cancellationToken))
            return new DeclarationReadBlocked(new GitLifecycleUnsupported());

        var current = await ReadDependenciesAsync(new(request.TenantId, request.Resource), session, cancellationToken);
        if (current.Count != request.Expected.Count || current.Any(item => !request.Expected.Any(expected =>
                expected.Dependent.ResourceId == item.Dependent.ResourceId &&
                expected.Dependent.Address.Kind == item.Dependent.Address.Kind &&
                ResourceGroupLifecycleVersions.Same(expected.Version, item.Version))))
            return new DeclarationPreconditionsChanged();

        var resolved = await ResolveRegistrationsAsync(request.TenantId, request.Resource.ResourceId, session,
            cancellationToken);
        if (resolved is null)
            return new DeclarationReadBlocked(new GitLifecycleResourceProtected());

        foreach (var item in resolved)
            if (registeredProviders.Value[item.Registration.ResourceTypeId]
                is IDeclarativeRegistrationOwnershipProvider ownership &&
                await ownership.FenceRegistrationOwnershipAsync(new(request.TenantId, item.Registration.ResourceId,
                    item.Version), session, cancellationToken) is not DeclarationFenced)
                return new DeclarationPreconditionsChanged();

        var group = await groups.Find(session,
            ResourceGroupFilters.ByTenantAndId(request.TenantId, request.Resource.ResourceId))
            .FirstOrDefaultAsync(cancellationToken);
        if (group is null)
            return current.Count == 0 ? new DeclarationFenced() : new DeclarationPreconditionsChanged();

        // Scope and registration admission both write the parent; even an empty reviewed set is fenced.
        var fenced = await groups.UpdateOneAsync(session, ResourceGroupFilters.AtRevisionAndRegistrationFence(group),
            Builders<ResourceGroup>.Update.Inc(item => item.RegistrationFence, 1),
            cancellationToken: cancellationToken);
        return fenced.ModifiedCount == 1 ? new DeclarationFenced() : new DeclarationPreconditionsChanged();
    }

    private async Task<IReadOnlyList<RegistrationDependency>?> ResolveRegistrationsAsync(string tenant, string group,
        IClientSessionHandle session, CancellationToken ct)
    {
        var current = await registrations.Find(session, ResourceRegistrationFilters.ByResourceGroup(tenant, group))
            .ToListAsync(ct);
        var result = new List<RegistrationDependency>();
        foreach (var item in current)
        {
            if (!registeredProviders.Value.TryGetValue(item.ResourceTypeId, out var provider))
                return null;

            var dependency = new RegistrationDependency(item, new(new(provider.Kind, ""), item.ResourceId),
                new([new($"ResourceRegistration/{item.Id}", item.ReferenceFence)]));
            if (provider is IDeclarativeRegistrationOwnershipProvider ownership)
            {
                var read = await ownership.ReadRegistrationOwnershipAsync(new(tenant, item.ResourceId), session, ct);
                var resolved = read switch
                {
                    IndependentRegisteredResourceOwnership independent => dependency with
                    {
                        Version = new([.. dependency.Version.Documents, .. independent.Version.Documents])
                    },
                    ManagedRegisteredResourceOwnership managed =>
                        await ResolveManagedRegistrationAsync(dependency, managed, session, ct),
                    DeclarationReadBlocked => null
                };
                if (resolved is null)
                    return null;

                dependency = resolved;
            }

            // Keep the original registration vector even when several rows map to the same owner.
            result.Add(dependency);
        }

        return result;
    }

    private async Task<RegistrationDependency?> ResolveManagedRegistrationAsync(RegistrationDependency dependency,
        ManagedRegisteredResourceOwnership managed, IClientSessionHandle session, CancellationToken ct)
    {
        if (!registeredProviders.Value.TryGetValue(managed.OwnerType.Value, out var ownerProvider))
            return null;

        var registration = dependency.Registration;
        var owner = await registrations.Find(session, ResourceRegistrationFilters.ByIdentity(registration.TenantId,
            managed.OwnerType, managed.OwnerId)).FirstOrDefaultAsync(ct);
        if (owner is null || owner.ResourceGroupId != registration.ResourceGroupId)
            return null;

        return dependency with
        {
            Dependent = new(new(ownerProvider.Kind, ""), owner.ResourceId),
            Version = new([.. dependency.Version.Documents, .. managed.Version.Documents])
        };
    }

    private sealed record RegistrationDependency(
        ResourceRegistration Registration,
        GitLifecycleResourceReference Dependent,
        DeclarationVersionToken Version);
}

internal static class ResourceGroupLifecycleVersions
{
    public static DeclarationVersionToken Group(ResourceGroup group) => new(
        [new($"ResourceGroup/{group.Id}", group.Revision),
            new($"ResourceGroupFence/{group.Id}", group.RegistrationFence)]);

    public static DeclarationVersionToken Scope(IsolationScope scope, ResourceGroup group) => new(
        [new($"IsolationScope/{scope.Id}", scope.Revision), .. Group(group).Documents]);

    public static bool Same(DeclarationVersionToken left, DeclarationVersionToken right) =>
        left.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal)
            .SequenceEqual(right.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal));
}
