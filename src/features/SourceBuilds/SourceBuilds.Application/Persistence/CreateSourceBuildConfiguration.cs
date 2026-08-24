using MongoDB.Driver;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Domain;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Persistence;

public delegate Task<Result<SourceBuildConfiguration, SourceBuildConfigurationAlreadyExists>>
    CreateSourceBuildConfiguration(
        SourceBuildConfiguration configuration,
        CancellationToken cancellationToken = default);

internal sealed class MongoCreateSourceBuildConfiguration(
    IMongoCollection<SourceBuildConfiguration> configurations)
{
    public async Task<Result<SourceBuildConfiguration, SourceBuildConfigurationAlreadyExists>>
        InvokeAsync(
            SourceBuildConfiguration configuration,
            CancellationToken cancellationToken = default)
    {
        try
        {
            await configurations.InsertOneAsync(configuration, cancellationToken: cancellationToken);
            return new Ok<SourceBuildConfiguration>(configuration);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new Err<SourceBuildConfigurationAlreadyExists>(
                new SourceBuildConfigurationAlreadyExists(configuration.WebServiceId));
        }
    }
}
