using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Mutations;

public delegate Task PersistCreatedFunctionApp(
    IClientSessionHandle session,
    FunctionAppFactory.CreateResult created,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedFunctionApp(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRevision> functionRevisions,
    IMongoCollection<FunctionRouteBinding> functionRouteBindings,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task InvokeAsync(
        IClientSessionHandle session,
        FunctionAppFactory.CreateResult created,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(created);

        await functionRouteBindings.InsertOneAsync(
            session,
            created.RouteBinding,
            cancellationToken: cancellationToken);
        await functionApps.InsertOneAsync(
            session,
            created.FunctionApp,
            cancellationToken: cancellationToken);
        await functionRevisions.InsertOneAsync(
            session,
            created.Revision,
            cancellationToken: cancellationToken);
        await storeOutboxWrites(
            session,
            [
                OutboxWrite.Create(created.FunctionApp.ToCreatedEvent(created.Revision)),
                OutboxWrite.Create(created.Revision.ToRevisionCreatedEvent(created.FunctionApp))
            ],
            cancellationToken);
    }
}
