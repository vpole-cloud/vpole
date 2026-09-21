using MongoDB.Driver;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

public delegate Task<Secret?> LoadSecretById(
    string tenantId,
    string id,
    CancellationToken ct);

internal sealed class MongoLoadSecretById(
    IMongoCollection<Secret> secrets)
{
    public Task<Secret?> InvokeAsync(
        string tenantId,
        string id,
        CancellationToken ct) =>
        secrets.Find(SecretFilters.Identity(tenantId, id)).FirstOrDefaultAsync(ct)!;
}
