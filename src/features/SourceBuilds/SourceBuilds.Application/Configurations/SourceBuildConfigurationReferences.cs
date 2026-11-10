using MongoDB.Driver;
using Pole.Features.SourceBuilds.Application.Mongo;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Configurations;

internal sealed class SourceBuildConfigurationReferences(
    IMongoCollection<SourceBuildConfiguration> configurations
)
{
    public Task<FindSourceBuildConfigurationOutcome> LoadAsync(
        SourceBuildConfigurationReferenceRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) => ReadAsync(
        configurations.Find(session, SourceBuildConfigurationFilters.ById(request.TenantId, request.ConfigurationId)),
        cancellationToken);

    public Task<FindSourceBuildConfigurationOutcome> FindNamedAsync(
        NamedSourceBuildConfigurationRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken) => ReadAsync(
        configurations.Find(session, SourceBuildConfigurationFilters.ByName(request.TenantId, request.TechnicalName)),
        cancellationToken);

    private async Task<FindSourceBuildConfigurationOutcome> ReadAsync(
        IFindFluent<SourceBuildConfiguration, SourceBuildConfiguration> query,
        CancellationToken cancellationToken)
    {
        var item = await query.FirstOrDefaultAsync(cancellationToken);
        return item is null ? new SourceBuildConfigurationReferenceMissing() : Snapshot(item);
    }

    public async Task<FenceSourceBuildConfigurationOutcome> FenceAsync(
        SourceBuildConfigurationSnapshot expected,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var result = await configurations.UpdateOneAsync(
            session,
            SourceBuildConfigurationFilters.AtSnapshot(expected),
            Builders<SourceBuildConfiguration>.Update.Inc(item => item.ReferenceFence, 1),
            cancellationToken: cancellationToken);
        return result.ModifiedCount == 1
            ? new SourceBuildConfigurationFenced()
            : new SourceBuildConfigurationReferenceConflict();
    }

    internal static SourceBuildConfigurationSnapshot Snapshot(SourceBuildConfiguration item) => new(
        item.Id,
        item.TenantId,
        item.TechnicalName,
        item.SourceRepositoryId,
        item.WebServiceId,
        item.Recipe.Dockerfile,
        item.Revision,
        item.BuildGeneration);
}
