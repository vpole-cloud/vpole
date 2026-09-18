using MongoDB.Driver;
using Pole.Database;
using Pole.Features.SourceBuilds.Domain;

namespace Pole.Features.SourceBuilds.Application.Persistence;

internal static class SourceBuildConfigurationFilters
{
    public static FilterDefinition<SourceBuildConfiguration> VisibleToTenant(string tenantId) =>
        Builders<SourceBuildConfiguration>.Filter.ForTenant(tenantId);

    public static FilterDefinition<SourceBuildConfiguration> ById(
        string tenantId,
        string configurationId) =>
        Builders<SourceBuildConfiguration>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<SourceBuildConfiguration>.Filter.Eq(item => item.Id, configurationId));

    public static FilterDefinition<SourceBuildConfiguration> ByWebService(
        string tenantId,
        string webServiceId) =>
        Builders<SourceBuildConfiguration>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<SourceBuildConfiguration>.Filter.Eq(item => item.WebServiceId, webServiceId));

    public static FilterDefinition<SourceBuildConfiguration> BySourceRepository(
        string tenantId,
        string sourceRepositoryId) =>
        Builders<SourceBuildConfiguration>.Filter.And(
            VisibleToTenant(tenantId),
            Builders<SourceBuildConfiguration>.Filter.Eq(
                item => item.SourceRepositoryId,
                sourceRepositoryId));

    public static FilterDefinition<SourceBuildConfiguration> AtUpdatedAt(
        SourceBuildConfiguration configuration) =>
        Builders<SourceBuildConfiguration>.Filter.And(
            ById(configuration.TenantId, configuration.Id),
            Builders<SourceBuildConfiguration>.Filter.Eq(
                item => item.UpdatedAtUtc,
                configuration.UpdatedAtUtc));
}

internal static class SourceBuildJobFilters
{
    public static FilterDefinition<SourceBuildJob> ById(string buildJobId) =>
        Builders<SourceBuildJob>.Filter.Eq(item => item.Id, buildJobId);

    public static FilterDefinition<SourceBuildJob> ById(string tenantId, string buildJobId) =>
        Builders<SourceBuildJob>.Filter.And(
            Builders<SourceBuildJob>.Filter.ForTenant(tenantId),
            ById(buildJobId));

    public static FilterDefinition<SourceBuildJob> ForList(SourceBuildJobListQuery query)
    {
        var filter = Builders<SourceBuildJob>.Filter.ForTenant(query.TenantId);
        if (!string.IsNullOrWhiteSpace(query.ConfigurationId))
        {
            filter &= Builders<SourceBuildJob>.Filter.Eq(
                item => item.ConfigurationId,
                query.ConfigurationId);
        }
        if (!string.IsNullOrWhiteSpace(query.WebServiceId))
        {
            filter &= Builders<SourceBuildJob>.Filter.Eq(
                item => item.WebServiceId,
                query.WebServiceId);
        }

        return filter;
    }

    public static FilterDefinition<SourceBuildJob> ActiveForWebService(string tenantId, string webServiceId) =>
        Builders<SourceBuildJob>.Filter.And(
            Builders<SourceBuildJob>.Filter.ForTenant(tenantId),
            Builders<SourceBuildJob>.Filter.Eq(item => item.WebServiceId, webServiceId),
            Builders<SourceBuildJob>.Filter.Not(TerminalStates()));

    public static FilterDefinition<SourceBuildJob> ActiveForConfiguration(
        string tenantId,
        string configurationId) =>
        Builders<SourceBuildJob>.Filter.And(
            Builders<SourceBuildJob>.Filter.ForTenant(tenantId),
            Builders<SourceBuildJob>.Filter.Eq(item => item.ConfigurationId, configurationId),
            Builders<SourceBuildJob>.Filter.Not(TerminalStates()));

    public static FilterDefinition<SourceBuildJob> ClaimQueued(string buildJobId) =>
        Builders<SourceBuildJob>.Filter.And(
            ById(buildJobId),
            Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, QueuedSourceBuild>(
                item => item.State));

    public static FilterDefinition<SourceBuildJob> AtUpdatedAt(SourceBuildJob job) =>
        Builders<SourceBuildJob>.Filter.And(
            ById(job.TenantId, job.Id),
            Builders<SourceBuildJob>.Filter.Eq(item => item.UpdatedAtUtc, job.UpdatedAtUtc));

    public static FilterDefinition<SourceBuildJob> StaleQueued(DateTime cutoff) =>
        Builders<SourceBuildJob>.Filter.And(
            Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, QueuedSourceBuild>(
                item => item.State),
            Builders<SourceBuildJob>.Filter.Lte(item => item.UpdatedAtUtc, cutoff));

    public static FilterDefinition<SourceBuildJob> DeployingRelease(
        string tenantId,
        string releaseId) =>
        Builders<SourceBuildJob>.Filter.And(
            Builders<SourceBuildJob>.Filter.ForTenant(tenantId),
            Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, DeployingSourceBuild>(
                item => item.State),
            Builders<SourceBuildJob>.Filter.Eq(
                item => ((DeployingSourceBuild)item.State).ReleaseId,
                releaseId));

    public static FilterDefinition<SourceBuildJob> DeployingReleaseAtUpdatedAt(
        SourceBuildJob job) =>
        Builders<SourceBuildJob>.Filter.And(
            AtUpdatedAt(job),
            Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, DeployingSourceBuild>(
                item => item.State));

    private static FilterDefinition<SourceBuildJob> TerminalStates() =>
        Builders<SourceBuildJob>.Filter.Or(
            Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, SucceededSourceBuild>(
                item => item.State),
            Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, FailedSourceBuild>(
                item => item.State));
}

public sealed record SourceBuildJobListQuery(
    string TenantId,
    string? ConfigurationId,
    string? WebServiceId,
    int PageNumber,
    int PageSize);
