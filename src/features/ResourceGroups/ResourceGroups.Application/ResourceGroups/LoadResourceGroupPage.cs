// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Mongo;
using Pole.Features.ResourceGroups.Domain;

namespace Pole.Features.ResourceGroups.Application.ResourceGroups;

public sealed record ResourceGroupPageQuery(
    string TenantId,
    int PageNumber,
    int PageSize,
    string? Search
);

public sealed record ResourceGroupPage(
    IReadOnlyList<ResourceGroupPageItem> Items,
    long TotalCount
);

public sealed record ResourceGroupPageItem(
    ResourceGroup ResourceGroup,
    long ResourceCount
);

public delegate Task<ResourceGroupPage> LoadResourceGroupPage(
    ResourceGroupPageQuery query,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadResourceGroupPage(
    IMongoCollection<ResourceGroup> resourceGroups,
    IMongoCollection<ResourceRegistration> registrations
)
{
    public async Task<ResourceGroupPage> InvokeAsync(
        ResourceGroupPageQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var filter = ResourceGroupFilters.Search(query.TenantId, query.Search);
        var totalCount = await resourceGroups.CountDocumentsAsync(filter, cancellationToken: cancellationToken);
        var items = await resourceGroups.Find(filter)
            .SortBy(group => group.Name)
            .ThenBy(group => group.Id)
            .Skip(checked((query.PageNumber - 1) * query.PageSize))
            .Limit(query.PageSize)
            .ToListAsync(cancellationToken);
        var groupIds = items.Select(group => group.Id).ToArray();
        var counts = groupIds.Length == 0
            ? []
            : await registrations.Aggregate()
                .Match(ResourceRegistrationFilters.ByResourceGroups(query.TenantId, groupIds))
                .Group(
                    registration => registration.ResourceGroupId,
                    group => new ResourceGroupRegistrationCount(group.Key, group.LongCount()))
                .ToListAsync(cancellationToken);
        var countByGroup = counts.ToDictionary(
            count => count.ResourceGroupId,
            count => count.Count,
            StringComparer.Ordinal);
        return new ResourceGroupPage(
            items.Select(group => new ResourceGroupPageItem(group, countByGroup.GetValueOrDefault(group.Id))).ToArray(),
            totalCount);
    }
}

internal sealed record ResourceGroupRegistrationCount(
    string ResourceGroupId,
    long Count
);
