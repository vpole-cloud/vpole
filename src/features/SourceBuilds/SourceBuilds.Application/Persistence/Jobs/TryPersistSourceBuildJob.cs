using MongoDB.Driver;
using Pole.Database;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Jobs;

public delegate Task<bool> TryPersistSourceBuildJob(
    SourceBuildJob current,
    SourceBuildJob next,
    CancellationToken cancellationToken = default);

internal sealed class MongoTryPersistSourceBuildJob(
    IMongoCollection<SourceBuildJob> jobs,
    MongoRunTransaction transactions,
    StoreOutboxWrites storeOutboxWrites)
{
    public Task<bool> InvokeAsync(
        SourceBuildJob current,
        SourceBuildJob next,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(current.Id, next.Id, StringComparison.Ordinal) ||
            !string.Equals(current.TenantId, next.TenantId, StringComparison.Ordinal) ||
            !string.Equals(current.WebServiceId, next.WebServiceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "A source build job replacement must preserve build-job, tenant and WebService identity.");
        }

        return transactions.InvokeOutcomeAsync<bool>(async (transaction, ct) =>
            {
                var updated = await jobs.UpdateOneAsync(
                    transaction.Session,
                    SourceBuildJobFilters.AtUpdatedAt(current) & SourceBuildJobFilters.LiveLease(current),
                    Builders<SourceBuildJob>.Update.Set(item => item.State, next.State)
                        .Set(item => item.UpdatedAtUtc, next.UpdatedAtUtc),
                    cancellationToken: ct);
                if (updated.ModifiedCount != 1)
                    return transaction.Commit(false);

                await storeOutboxWrites(
                    transaction.Session,
                    [OutboxWrite.Create(next.ToStateChangedEvent())],
                    ct);
                return transaction.Commit(true);
            },
            cancellationToken);
    }
}
