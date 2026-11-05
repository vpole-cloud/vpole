using MongoDB.Driver;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Values;

public delegate Task<SecretVersion?> LoadSecretVersion(
    string tenantId,
    string secretId,
    long version,
    CancellationToken ct);

internal sealed class MongoLoadSecretVersion(IMongoCollection<SecretVersion> versions)
{
    public Task<SecretVersion?> InvokeAsync(
        string tenantId,
        string secretId,
        long version,
        CancellationToken ct) => versions.Find(SecretFilters.Version(tenantId, secretId, version))
            .FirstOrDefaultAsync(ct)!;

}
