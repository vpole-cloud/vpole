using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application;
using MongoDB.Driver;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Managed;

public delegate Task<Secret?> LoadManagedSecret(
    string tenantId,
    ManagedSecretOwner owner,
    CancellationToken ct);

internal sealed class MongoLoadManagedSecret(IMongoCollection<Secret> secrets)
{
    public Task<Secret?> InvokeAsync(string tenantId, ManagedSecretOwner owner, CancellationToken ct) =>
        secrets.Find(ManagedSecretFilters.Owner(tenantId, owner)).FirstOrDefaultAsync(ct)!;
}
