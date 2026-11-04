using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.GitOps.Contracts;
using Pole.Features.Events.Abstractions;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Events;

namespace Pole.Features.Functions.Application.Persistence.Lifecycle;

public sealed record FunctionAppDeletionChanged(string FunctionAppId);

public union BeginFunctionAppDeletionOutcome(
    FunctionApp,
    FunctionAppDeletionChanged,
    FunctionAppGitOwnedConfiguration);

public sealed record BeginFunctionAppDeletionRequest(
    FunctionApp Current,
    DateTimeOffset NowUtc);

public delegate Task<BeginFunctionAppDeletionOutcome> BeginFunctionAppDeletion(
    BeginFunctionAppDeletionRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

internal sealed class MongoBeginFunctionAppDeletion(
    FenceRegisteredResource fenceRegisteredResource,
    CascadeConnectionsForResourceDeletion cascadeConnections,
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRouteBinding> functionRouteBindings,
    IMongoCollection<FunctionTimerState> functionTimerStates,
    StoreOutboxWrites storeOutboxWrites,
    IGitOpsMutations git)
{
    public async Task<BeginFunctionAppDeletionOutcome> InvokeAsync(
        BeginFunctionAppDeletionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var (current, nowUtc) = request;
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Function App deletion requires an active transaction.");

        if (!await git.AuthorizeAsync(new(current.TenantId, "FunctionApp", current.Id), session, cancellationToken))
            return new FunctionAppGitOwnedConfiguration(current.Id);

        var registration = await fenceRegisteredResource(
            new FenceRegisteredResourceRequest(
                TenantId: current.TenantId,
                ResourceTypeId: FunctionResourceDeployment.ResourceType,
                ResourceId: current.Id),
            session: session,
            cancellationToken: cancellationToken);
        if (registration is null)
            return new FunctionAppDeletionChanged(current.Id);
        var cascade = await cascadeConnections(
            new CascadeConnectionsForResourceDeletionRequest(
                TenantId: current.TenantId,
                ResourceType: FunctionResourceDeployment.ResourceType,
                ResourceId: current.Id),
            session: session,
            cancellationToken: cancellationToken);
        FunctionAppDeletionChanged? cascadeConflict = cascade switch
        {
            ResourceConnectionsCascaded => null,
            ResourceConnectionCascadeConflict => new FunctionAppDeletionChanged(current.Id)
        };
        if (cascadeConflict is not null)
            return cascadeConflict;

        var appResult = await functionApps.UpdateOneAsync(
            session,
            FunctionAppFilters.ForDeletion(current),
            Builders<FunctionApp>.Update
                .Set(app => app.DesiredState, FunctionAppDesiredState.Deleting)
                .Set(app => app.Status, FunctionAppStatus.Deleting)
                .Set(app => app.UpdatedAtUtc, nowUtc),
            cancellationToken: cancellationToken);
        if (appResult.MatchedCount != 1)
        {
            return new FunctionAppDeletionChanged(current.Id);
        }

        await functionRouteBindings.UpdateOneAsync(
            session,
            FunctionRouteBindingFilters.ByFunctionApp(current),
            Builders<FunctionRouteBinding>.Update
                .Set(route => route.State, FunctionRouteBindingState.Disabled)
                .Set(route => route.UpdatedAtUtc, nowUtc),
            cancellationToken: cancellationToken);
        await functionTimerStates.UpdateManyAsync(
            session,
            FunctionTimerFilters.EnabledByFunctionApp(current),
            Builders<FunctionTimerState>.Update
                .Set(timer => timer.Enabled, false)
                .Set(timer => timer.Lease, null)
                .Set(timer => timer.UpdatedAtUtc, nowUtc),
            cancellationToken: cancellationToken);
        await storeOutboxWrites(
            session,
            [OutboxWrite.Create(
                new FunctionAppDeleteRequestedEvent(
                    current.Id,
                    current.Placement.ToSnapshot(),
                    current.ActiveRevisionId)
                {
                    EventCore = EventCore.Create(current.TenantId)
                })],
            cancellationToken);
        return current;
    }
}
