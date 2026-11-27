// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.SourceBuilds.Application.Mongo;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Configurations;

internal sealed class MongoEnsureNoWebServiceActiveSourceBuilds(
    IMongoCollection<SourceBuildJob> jobs
)
{
    public async Task<EnsureNoWebServiceActiveSourceBuildsOutcome> InvokeAsync(
        EnsureNoWebServiceActiveSourceBuildsRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var hasActiveJobs = await jobs.Find(
                session,
                SourceBuildJobFilters.ActiveForWebService(request.TenantId, request.WebServiceId))
            .AnyAsync(cancellationToken);
        return hasActiveJobs
            ? new SourceBuildWebServiceHasActiveJobs(request.WebServiceId)
            : new NoActiveSourceBuilds();
    }
}
