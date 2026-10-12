using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence.Configuration;

public union PersistCreatedSourceBuildConfigurationOutcome(
    SourceBuildConfiguration,
    SourceBuildConfigurationAlreadyExists,
    SourceBuildConfigurationWebServiceNotFound);

public delegate Task<PersistCreatedSourceBuildConfigurationOutcome>
    CreateSourceBuildConfiguration(
        SourceBuildConfiguration configuration,
        CancellationToken cancellationToken = default);

internal sealed class MongoCreateSourceBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations,
    MongoRunTransaction transactions,
    AdmitSourceBuildWebService admitWebService,
    StoreOutboxWrites outbox)
{
    public async Task<PersistCreatedSourceBuildConfigurationOutcome>
        InvokeAsync(
            SourceBuildConfiguration configuration,
            CancellationToken cancellationToken = default)
    {
        return await transactions.InvokeOutcomeAsync<PersistCreatedSourceBuildConfigurationOutcome>(
                async (transaction, ct) =>
                {
                    var admitted = await admitWebService(
                        configuration.TenantId,
                        configuration.WebServiceId,
                        transaction.Session,
                        ct);
                    if (admitted is SourceBuildWebServiceNotFound)
                    {
                        PersistCreatedSourceBuildConfigurationOutcome rejected =
                            new SourceBuildConfigurationWebServiceNotFound(configuration.WebServiceId);
                        return transaction.Abort(rejected);
                    }

                    try
                    {
                        await configurations.InsertOneAsync(transaction.Session, configuration, cancellationToken: ct);
                    }
                    catch (MongoWriteException exception) when (exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
                    {
                        return transaction.Abort(new SourceBuildConfigurationAlreadyExists(configuration.WebServiceId));
                    }
                    await outbox(transaction.Session, [OutboxWrite.Create(configuration.ToChangedEvent())], ct);

                    PersistCreatedSourceBuildConfigurationOutcome created = configuration;
                    return transaction.Commit(created);
                },
                cancellationToken);
    }
}
