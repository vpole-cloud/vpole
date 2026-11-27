// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.ResourceGroups.Contracts;

namespace Pole.Features.ResourceGroups.Application.ResourceRegistrations;

internal sealed class BoundResourceRegistrar(
    MongoResourceRegistrationMutations mutations,
    string? owningPluginId
) : IResourceRegistrar
{
    public Task<ResourceRegistrationOutcome> RegisterAsync(
        RegisterResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) => mutations.RegisterAsync(
        owningPluginId,
        request.TenantId,
        request.ResourceGroupId,
        request.Resource,
        session,
        cancellationToken);

    public Task<ResourceRegistrationOutcome> UpdateAsync(
        UpdateRegisteredResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) => mutations.UpdateAsync(
        owningPluginId,
        request.TenantId,
        request.ResourceGroupId,
        request.Resource,
        session,
        cancellationToken);

    public Task<ResourceUnregistrationOutcome> UnregisterAsync(
        UnregisterResourceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default) => mutations.UnregisterAsync(
        owningPluginId,
        request.TenantId,
        request.ResourceGroupId,
        request.ResourceTypeId,
        request.ResourceId,
        session,
        cancellationToken);
}
