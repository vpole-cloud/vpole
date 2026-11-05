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
using Pole.Features.Secrets.Application.Values;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Secrets;

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
