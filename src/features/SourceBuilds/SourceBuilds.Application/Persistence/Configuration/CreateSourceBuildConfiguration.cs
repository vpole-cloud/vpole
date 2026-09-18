using MongoDB.Driver;
using Pole.Database;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

public union PersistSourceBuildConfigurationError(SourceBuildConfigurationAlreadyExists, SourceBuildConfigurationWebServiceNotFound);

public delegate Task<Result<SourceBuildConfiguration, PersistSourceBuildConfigurationError>>
    CreateSourceBuildConfiguration(
        SourceBuildConfiguration configuration,
        CancellationToken cancellationToken = default);

internal sealed class MongoCreateSourceBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations,
    MongoRunTransaction transactions,
    AdmitSourceBuildWebService admitWebService)
{
    public async Task<Result<SourceBuildConfiguration, PersistSourceBuildConfigurationError>>
        InvokeAsync(
            SourceBuildConfiguration configuration,
            CancellationToken cancellationToken = default)
    {
        try
        {
            return await transactions.InvokeOutcomeAsync<Result<SourceBuildConfiguration, PersistSourceBuildConfigurationError>>(async (transaction, ct) =>
            {
                var admitted = await admitWebService(configuration.TenantId, configuration.WebServiceId, transaction.Session, ct);
                if (!admitted.TryGetValue(out _, out _))
                    return transaction.Abort(new Err<PersistSourceBuildConfigurationError>(
                        new(new SourceBuildConfigurationWebServiceNotFound(configuration.WebServiceId))));
                await configurations.InsertOneAsync(transaction.Session, configuration, cancellationToken: ct);
                return transaction.Commit(new Ok<SourceBuildConfiguration>(configuration));
            }, cancellationToken);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new Err<PersistSourceBuildConfigurationError>(
                new(new SourceBuildConfigurationAlreadyExists(configuration.WebServiceId)));
        }
    }
}
