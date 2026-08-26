using MongoDB.Driver;
using Pole.Database;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

public union DeleteSourceBuildConfigurationError(
    SourceBuildConfigurationNotFound,
    SourceBuildConfigurationHasActiveJobs);

public delegate Task<Result<Unit, DeleteSourceBuildConfigurationError>>
    DeleteSourceBuildConfiguration(
        string tenantId,
        string configurationId,
        CancellationToken cancellationToken = default);

internal sealed class MongoDeleteSourceBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations,
    IMongoCollection<SourceBuildJob> jobs,
    MongoRunTransaction transactions)
{
    public Task<Result<Unit, DeleteSourceBuildConfigurationError>> InvokeAsync(
        string tenantId,
        string configurationId,
        CancellationToken cancellationToken = default) =>
        transactions.InvokeOutcomeAsync<Result<Unit, DeleteSourceBuildConfigurationError>>(async (transaction, ct) =>
        {
            var hasActiveJobs = await jobs
                .Find(transaction.Session,
                    SourceBuildJobFilters.ActiveForConfiguration(tenantId, configurationId))
                .AnyAsync(ct);
            if (hasActiveJobs)
            {
                return transaction.Abort(new Err<DeleteSourceBuildConfigurationError>(
                    new(new SourceBuildConfigurationHasActiveJobs(configurationId))));
            }

            var deleted = await configurations.DeleteOneAsync(
                transaction.Session,
                SourceBuildConfigurationFilters.ById(tenantId, configurationId),
                cancellationToken: ct);
            return deleted.DeletedCount == 1
                ? transaction.Commit(new Ok<Unit>(Unit.Value))
                : transaction.Abort(new Err<DeleteSourceBuildConfigurationError>(
                    new(new SourceBuildConfigurationNotFound(configurationId))));
        }, cancellationToken);
}
