// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Tenants.Application.Mongo;
using Pole.Features.Tenants.Contracts;
using Pole.Features.Tenants.Domain;

namespace Pole.Features.Tenants.Application.Identity;

internal sealed class MongoFenceTenantIdentity(
    IMongoCollection<Tenant> tenants
)
{
    public async Task<LoadTenantIdentityOutcome> InvokeAsync(
        FenceTenantIdentityRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Tenant availability fencing requires a Mongo transaction.");

        var tenant = await tenants.FindOneAndUpdateAsync(
            session,
            TenantFilters.ById(request.TenantId),
            Builders<Tenant>.Update.Inc(item => item.ReferenceFence, 1),
            new FindOneAndUpdateOptions<Tenant> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        return tenant is null
            ? new TenantIdentityNotFound(request.TenantId)
            : new TenantIdentitySnapshot(tenant.Id, tenant.Name);
    }
}
