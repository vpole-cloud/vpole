// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.WebServices.Application.Mongo;
using Pole.Features.WebServices.Domain;

namespace Pole.Features.WebServices.Application.Queries;

internal sealed class MongoWebServiceExists(
    IMongoCollection<WebService> webServices
)
{
    public Task<bool> InvokeAsync(
        string tenantId,
        string webServiceId,
        CancellationToken cancellationToken = default) => webServices
        .Find(WebServiceFilters.ById(tenantId, webServiceId))
        .AnyAsync(cancellationToken);
}
