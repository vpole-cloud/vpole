using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence;
using Pole.Features.Functions.Domain;
using Pole.ResultType;

namespace Pole.Features.Functions.Application.Persistence.Mutations;

public sealed record FunctionAppRevisionChanged(string FunctionAppId);

public union PersistFunctionAppRevisionError(
    FunctionAppRevisionChanged);

public delegate Task<Result<FunctionRevision, PersistFunctionAppRevisionError>>
    PersistFunctionAppRevision(
        IClientSessionHandle session,
        FunctionApp current,
        FunctionRevision candidate,
        FunctionQuotaAllocation admittedQuota,
        ImmutableHashSet<string> tags,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default);

internal sealed class MongoPersistFunctionAppRevision(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRevision> functionRevisions,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task<Result<FunctionRevision, PersistFunctionAppRevisionError>> InvokeAsync(
        IClientSessionHandle session,
        FunctionApp current,
        FunctionRevision candidate,
        FunctionQuotaAllocation admittedQuota,
        ImmutableHashSet<string> tags,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken = default)
    {
        var update = Builders<FunctionApp>.Update
            .Set(app => app.PendingRevisionId, candidate.Id)
            .Set(app => app.NextRevisionNumber, checked(current.NextRevisionNumber + 1))
            .Set(
                app => app.Status,
                current.ActiveRevisionId is null
                    ? FunctionAppStatus.Provisioning
                    : FunctionAppStatus.Updating)
            .Set(app => app.AdmittedQuota, admittedQuota)
            .Set(app => app.Tags, tags)
            .Set(app => app.UpdatedAtUtc, updatedAtUtc);
        var updated = await functionApps.UpdateOneAsync(
            session,
            FunctionAppFilters.ForPendingRevision(current),
            update,
            cancellationToken: cancellationToken);
        if (updated.MatchedCount != 1)
        {
            return new Err<PersistFunctionAppRevisionError>(
                new FunctionAppRevisionChanged(current.Id));
        }

        await functionRevisions.InsertOneAsync(
            session,
            candidate,
            cancellationToken: cancellationToken);
        await storeOutboxWrites(
            session,
            [OutboxWrite.Create(candidate.ToRevisionCreatedEvent())],
            cancellationToken);
        return new Ok<FunctionRevision>(candidate);
    }
}
