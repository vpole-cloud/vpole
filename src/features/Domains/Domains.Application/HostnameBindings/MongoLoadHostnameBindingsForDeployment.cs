// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.HostnameBindings;

internal sealed class MongoLoadHostnameBindingsForDeployment(
    IMongoCollection<HostnameBinding> bindings
)
{
    public async Task<IReadOnlyList<HostnameBindingSnapshot>> InvokeAsync(
        LoadHostnameBindingsForDeploymentRequest request,
        CancellationToken cancellationToken = default)
    {
        var persistedBindings = await bindings.Find(
                DomainFilters.BindingForDeployment(
                    request.TenantId,
                    request.ResourceType,
                    request.ResourceId,
                    request.ResourceDeploymentId))
            .SortBy(item => item.Hostname)
            .ThenBy(item => item.Id)
            .ToListAsync(cancellationToken);
        return persistedBindings.Select(DomainContractMappings.ToSnapshot).ToArray();
    }
}
