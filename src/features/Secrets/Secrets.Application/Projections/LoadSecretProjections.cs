using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application;
using MongoDB.Driver;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Projections;

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
