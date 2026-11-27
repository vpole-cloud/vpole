// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.StorageProfiles.Application.Catalog;
using Pole.Features.StorageProfiles.Application.Mongo;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Domain;
using Pole.Workloads;

namespace Pole.Features.StorageProfiles.Application.Resolution;

internal sealed class MongoResolveNamedStorageProfile(
    IMongoCollection<StorageProfile> profiles,
    ResolveStorageProfile resolve
)
{
    public async Task<ResolveNamedStorageProfileOutcome> InvokeAsync(
        NamedStorageProfileRequest request,
        CancellationToken ct)
    {
        ResourceTechnicalName.Require(request.Name);
        if (request.Revision <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "An explicit positive revision is required.");

        var profile = await profiles.Find(StorageProfileFilters.ByTechnicalName(request.Name)).FirstOrDefaultAsync(ct);
        if (profile is null || !MongoLoadAvailableStorageProfiles.AllowsTenant(profile, request.TenantId))
            return new NamedStorageProfileUnavailable(request.Name);

        var result = await resolve(
            new StorageProfileResolutionRequest(
                profile.Id,
                request.DeploymentTargetId,
                request.TenantId,
                request.ResourceType,
                request.SizeGb,
                request.Revision),
            ct);
        return result switch
        {
            ResolvedStorageProfile storage => storage,
            StorageProfileResolutionError error => error,
        };
    }
}
