using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Endpoints;

internal static class GitProviderRepositoryAccess
{
    internal static async Task<GitProviderConnection?> GetConnectionAsync(
        IGitProviderDbContext dbContext,
        string tenantId,
        string connectionId,
        CancellationToken cancellationToken) =>
        await dbContext.GitProviderConnections
            .Find(Builders<GitProviderConnection>.Filter.And(
                Builders<GitProviderConnection>.Filter.ForTenant(tenantId),
                Builders<GitProviderConnection>.Filter.Eq(item => item.Id, connectionId)))
            .FirstOrDefaultAsync(cancellationToken);
}
