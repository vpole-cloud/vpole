// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.GitProviders.Application.Mongo;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Connections;

internal sealed class MongoFindNamedGitProviderConnection(
    IMongoCollection<GitProviderConnection> gitProviderConnections
)
{
    public async Task<FindNamedGitProviderConnectionOutcome> InvokeAsync(
        NamedGitProviderConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var connection = await gitProviderConnections
            .Find(GitProviderFilters.ConnectionByTenantAndName(request.TenantId, request.TechnicalName))
            .FirstOrDefaultAsync(cancellationToken);

        return connection is null
            ? new NamedGitProviderConnectionNotFound(request.TechnicalName)
            : new NamedGitProviderConnectionFound(connection.Id);
    }
}
