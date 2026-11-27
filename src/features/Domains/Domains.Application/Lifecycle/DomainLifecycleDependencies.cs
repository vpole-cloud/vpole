// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Domains.Application.Lifecycle;

internal sealed class DomainLifecycleDependencies(
    IMongoCollection<TenantDomainZone> zones,
    IMongoCollection<HostnameBinding> bindings,
    FenceDomainZone fenceZone,
    FenceHostnameBinding fenceBinding,
    IEnumerable<IHostnameBindingWorkloadCapability> workloads
) : IDeclarativeLifecycleDependencyProvider
{
    private readonly IReadOnlyDictionary<string, IHostnameBindingWorkloadCapability> capabilities
        = workloads.ToDictionary(item => item.Kind, StringComparer.Ordinal);

    public string DependencyOwner => "Domains";

    public async Task<IReadOnlyList<GitLifecycleDependency>> ReadDependenciesAsync(
        ReadLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        FilterDefinition<HostnameBinding> filter;
        if (request.Resource.Address.Kind == "DomainZone")
            filter = DomainFilters.BindingsForTenantZone(request.TenantId, request.Resource.ResourceId);
        else if (capabilities.TryGetValue(request.Resource.Address.Kind, out var capability))
            filter = DomainFilters.BindingsForResource(
                request.TenantId,
                capability.ResourceType,
                request.Resource.ResourceId);
        else
            return [];

        var rows = await bindings.Find(session, filter).ToListAsync(cancellationToken);
        return rows.Where(item => item.Identity is NamedDomainIdentity)
            .Select(item => new GitLifecycleDependency(
                new GitLifecycleResourceReference(
                    new ResourceAddress("HostnameBinding", ((NamedDomainIdentity)item.Identity).TechnicalName),
                    item.Id),
                request.Resource,
                DomainLifecycleVersions.Binding(item)))
            .OrderBy(item => item.Dependent.ResourceId, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<FenceDeclarationOutcome> FenceDependenciesAsync(
        FenceLifecycleDependenciesRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Domain dependency fencing requires a transaction.");
        var current = await ReadDependenciesAsync(
            new ReadLifecycleDependenciesRequest(request.TenantId, request.Resource),
            session,
            cancellationToken);
        if (current.Count != request.Expected.Count || current.Any(item => !request.Expected.Any(expected =>
                expected.Dependent == item.Dependent && DomainLifecycleVersions.Same(expected.Version, item.Version))))
            return new DeclarationPreconditionsChanged();
        if (request.Resource.Address.Kind == "DomainZone")
        {
            var zone = await zones.Find(session, DomainFilters.ZoneById(request.TenantId, request.Resource.ResourceId))
                .FirstOrDefaultAsync(cancellationToken);
            if (zone is not null && await fenceZone(
                    new FenceDomainZoneRequest(request.TenantId, zone.Id, zone.Revision, zone.ReferenceFence),
                    session,
                    cancellationToken) is not DomainZoneFenced)
                return new DeclarationPreconditionsChanged();
        }

        foreach (var dependency in current)
        {
            var binding = await bindings.Find(
                    session,
                    DomainFilters.BindingById(request.TenantId, dependency.Dependent.ResourceId))
                .SingleAsync(cancellationToken);
            if (await fenceBinding(
                    new FenceHostnameBindingRequest(request.TenantId, binding.Id, binding.ReferenceFence),
                    session,
                    cancellationToken) is not HostnameBindingFenced)
                return new DeclarationPreconditionsChanged();
        }

        return new DeclarationFenced();
    }
}

internal static class DomainLifecycleVersions
{
    internal static string ZoneRevisionKey(string id) => $"DomainZone/{id}";

    internal static string ZoneFenceKey(string id) => $"DomainZoneFence/{id}";

    internal static string ZoneReferenceFenceKey(string id) => $"{ZoneRevisionKey(id)}/ReferenceFence";

    internal static string BindingReferenceFenceKey(string id) => $"HostnameBinding/{id}";

    public static DeclarationVersionToken Zone(TenantDomainZone zone) => new(
    [
        new DeclarationDocumentVersion(ZoneRevisionKey(zone.Id), zone.Revision),
        new DeclarationDocumentVersion(ZoneFenceKey(zone.Id), zone.ReferenceFence),
    ]);

    public static DeclarationVersionToken Binding(HostnameBinding binding) => new(
        [new DeclarationDocumentVersion(BindingReferenceFenceKey(binding.Id), binding.ReferenceFence)]);

    public static bool Same(DeclarationVersionToken left, DeclarationVersionToken right) => left.Documents
        .OrderBy(item => item.DocumentId, StringComparer.Ordinal)
        .SequenceEqual(right.Documents.OrderBy(item => item.DocumentId, StringComparer.Ordinal));
}
