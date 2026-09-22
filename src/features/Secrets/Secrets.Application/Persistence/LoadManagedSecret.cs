using MongoDB.Driver;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Persistence;

public delegate Task<Secret?> LoadManagedSecret(
    string tenantId,
    ManagedSecretOwner owner,
    CancellationToken ct);

internal sealed class MongoLoadManagedSecret(IMongoCollection<Secret> secrets)
{
    public Task<Secret?> InvokeAsync(string tenantId, ManagedSecretOwner owner, CancellationToken ct) =>
        secrets.Find(ManagedSecretFilters.Owner(tenantId, owner)).FirstOrDefaultAsync(ct)!;
}
