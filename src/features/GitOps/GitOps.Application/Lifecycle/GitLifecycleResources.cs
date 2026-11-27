// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Features.GitOps.Application.Mongo;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;

namespace Pole.Features.GitOps.Application.Lifecycle;

public sealed class GitLifecycleResources(
    DeclarativeProviderRegistry providers,
    IEnumerable<IDeclarativeLifecycleDependencyProvider> dependencies,
    IMongoCollection<ManagedDeclaration> inventory
)
{
    private readonly IReadOnlyDictionary<string, IDeclarativeLifecycleDependencyProvider> contributors
        = dependencies.ToDictionary(item => item.DependencyOwner, StringComparer.Ordinal);

    public Task<ReadLifecycleResourceOutcome> ReadAsync(
        string tenant,
        GitResourceIdentity resource,
        IClientSessionHandle session,
        CancellationToken ct) => providers.Find(resource.Kind) is IDeclarativeResourceLifecycleProvider provider
        ? provider.ReadLifecycleAsync(new ReadLifecycleResourceRequest(tenant, Reference(resource)), session, ct)
        : Task.FromResult<ReadLifecycleResourceOutcome>(new DeclarationReadBlocked(new GitLifecycleUnsupported()));

    public async Task<IReadOnlyList<GitPreviewDependencySet>> DependencySetsAsync(
        string tenant,
        GitResourceIdentity resource,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var result = new List<GitPreviewDependencySet>();
        foreach (var (owner, provider) in contributors)
        {
            var references = await provider.ReadDependenciesAsync(
                new ReadLifecycleDependenciesRequest(tenant, Reference(resource)),
                session,
                ct);
            var normalized = new List<GitPreviewDependency>();
            foreach (var item in references)
            {
                var declaration = await inventory.Find(
                        session,
                        ManagedDeclarationFilters.Resource(
                            tenant,
                            item.Dependent.Address.Kind,
                            item.Dependent.ResourceId))
                    .FirstOrDefaultAsync(ct);
                normalized.Add(
                    new GitPreviewDependency(
                        new GitResourceIdentity(
                            item.Dependent.Address.Kind,
                            declaration?.Name ?? item.Dependent.Address.Name,
                            item.Dependent.ResourceId),
                        item.Version
                            .Documents
                            .Select(version => new GitResourceVersion(version.DocumentId, version.Revision))
                            .ToImmutableArray(),
                        item.Order));
            }

            result.Add(new GitPreviewDependencySet(owner, resource, normalized.ToImmutableArray()));
        }

        return result;
    }

    public async Task<IReadOnlyList<GitLifecycleDependency>> DependenciesAsync(
        string tenant,
        GitResourceIdentity resource,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        var result = new List<GitLifecycleDependency>();
        foreach (var provider in contributors.Values)
            result.AddRange(
                await provider.ReadDependenciesAsync(
                    new ReadLifecycleDependenciesRequest(tenant, Reference(resource)),
                    session,
                    ct));
        return result.DistinctBy(item => (item.Dependent, item.Target)).ToArray();
    }

    public async Task<FenceDeclarationOutcome> FenceDependenciesAsync(
        string tenant,
        GitResourceIdentity resource,
        IReadOnlyList<GitPreviewDependencySet> expected,
        IClientSessionHandle session,
        CancellationToken ct)
    {
        foreach (var (owner, provider) in contributors)
        {
            var reviewed = expected.SingleOrDefault(item => item.Owner == owner && item.Target == resource);
            if (reviewed is null)
                return new DeclarationPreconditionsChanged();

            var references = reviewed.Dependencies
                .Select(item => new GitLifecycleDependency(
                    Reference(item.Dependent),
                    Reference(resource),
                    new DeclarationVersionToken(
                        item.Versions
                            .Select(version => new DeclarationDocumentVersion(version.DocumentId, version.Revision))
                            .ToArray()),
                    item.Order))
                .ToArray();
            var outcome = await provider.FenceDependenciesAsync(
                new FenceLifecycleDependenciesRequest(tenant, Reference(resource), references),
                session,
                ct);
            if (outcome is not DeclarationFenced)
                return outcome;
        }

        return new DeclarationFenced();
    }

    public static GitLifecycleResourceReference Reference(GitResourceIdentity resource) => new(
        new ResourceAddress(resource.Kind, resource.Name),
        resource.ResourceId);

    public static GitResourcePreconditions Expected(
        GitResourceIdentity resource,
        string? owner,
        GitLifecycleResourceSnapshot snapshot) => new(
        resource,
        owner,
        snapshot.Version
            .Documents
            .Select(item => new GitResourceVersion(item.DocumentId, item.Revision))
            .ToImmutableArray(),
        LifecycleHash(snapshot));

    public static bool Same(GitResourcePreconditions? expected, GitResourcePreconditions? current) =>
        expected is null || current is null
            ? expected is null && current is null
            : expected.Resource == current.Resource && expected.OwnerSourceId == current.OwnerSourceId &&
            expected.ConfigurationHash == current.ConfigurationHash && expected.Versions
                .OrderBy(item => item.DocumentId, StringComparer.Ordinal)
                .SequenceEqual(current.Versions.OrderBy(item => item.DocumentId, StringComparer.Ordinal));

    private static string LifecycleHash(GitLifecycleResourceSnapshot snapshot) =>
        DeclarationPlanValues.Hash($"{snapshot.Resource.ResourceId}/{snapshot.Presence}");
}
