// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Domains.Application.Common;
using Pole.Features.Domains.Application.Mongo;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.HostnameBindings;

internal sealed class MongoLoadHostnameBindingsForDeploymentInTransaction(
    IMongoCollection<HostnameBinding> bindings
)
{
    public async Task<IReadOnlyList<HostnameBindingSnapshot>> InvokeAsync(
        LoadHostnameBindingsForDeploymentInTransactionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        DomainTransactions.EnsureActive(session);
        var persistedBindings = await bindings.Find(
                session,
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
