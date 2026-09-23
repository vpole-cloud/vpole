using MongoDB.Driver;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

public delegate Task<IReadOnlyList<SecretProjection>> LoadSecretProjections(
    string tenantId,
    string secretId,
    CancellationToken ct);

internal sealed class MongoLoadSecretProjections(IMongoCollection<SecretProjection> projections)
{
    public async Task<IReadOnlyList<SecretProjection>> InvokeAsync(
        string tenantId,
        string secretId,
        CancellationToken ct) => await projections
            .Find(SecretProjectionFilters.ForSecret(tenantId, secretId))
            .ToListAsync(ct);
}
