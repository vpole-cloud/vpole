using MongoDB.Driver;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

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
