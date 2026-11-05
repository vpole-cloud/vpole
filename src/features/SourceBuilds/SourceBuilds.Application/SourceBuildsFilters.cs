using MongoDB.Driver;
using Pole.Features.SourceBuilds.Contracts;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.Database;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.SourceBuilds.Application.Logs;

namespace Pole.Features.SourceBuilds.Application;

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

    public static FilterDefinition<SourceBuildConfiguration> ByName(string tenantId, string name) =>
        VisibleToTenant(tenantId) & Builders<SourceBuildConfiguration>.Filter.Eq(item => item.TechnicalName, name);

    public static FilterDefinition<SourceBuildConfiguration> AtSnapshot(SourceBuildConfigurationSnapshot expected) =>
        ById(expected.TenantId, expected.Id) &
        Builders<SourceBuildConfiguration>.Filter.Eq(item => item.Revision, expected.Revision) &
        Builders<SourceBuildConfiguration>.Filter.Eq(item => item.SourceRepositoryId, expected.SourceRepositoryId) &
        Builders<SourceBuildConfiguration>.Filter.Eq(item => item.WebServiceId, expected.WebServiceId);

    public static FilterDefinition<SourceBuildConfiguration> ForQueue(SourceBuildJob job) =>
        ForInput(job, job.InputVersion.Generation - 1);

    public static FilterDefinition<SourceBuildConfiguration> ForPromotion(SourceBuildJob job) =>
        ForInput(job, job.InputVersion.Generation);

    private static FilterDefinition<SourceBuildConfiguration> ForInput(SourceBuildJob job, long expectedGeneration) =>
        ById(job.TenantId, job.ConfigurationId) &
        Builders<SourceBuildConfiguration>.Filter.Eq(item => item.Revision, job.InputVersion.ConfigurationRevision) &
        Builders<SourceBuildConfiguration>.Filter.Eq(item => item.BuildGeneration, expectedGeneration) &
        Builders<SourceBuildConfiguration>.Filter.Eq(item => item.SourceRepositoryId, job.SourceRepositoryId) &
        Builders<SourceBuildConfiguration>.Filter.Eq(item => item.WebServiceId, job.WebServiceId);

    public static FilterDefinition<SourceBuildConfiguration> AtPlanningVersion(SourceBuildConfiguration current) =>
        AtRevision(current) &
        Builders<SourceBuildConfiguration>.Filter.Eq(item => item.ReferenceFence, current.ReferenceFence) &
        Builders<SourceBuildConfiguration>.Filter.Eq(item => item.BuildGeneration, current.BuildGeneration);

    public static FilterDefinition<SourceBuildConfiguration> AtRevision(
        SourceBuildConfiguration configuration) =>
        Builders<SourceBuildConfiguration>.Filter.And(
            ById(configuration.TenantId, configuration.Id),
            Builders<SourceBuildConfiguration>.Filter.Eq(
                item => item.Revision,
                configuration.Revision));
}

internal static class SourceBuildJobFilters
{
    public static FilterDefinition<SourceBuildJob> OwnedLease(SourceBuildExecutionIdentity identity) =>
        SourceBuildJobFilters.ById(identity.TenantId, identity.BuildJobId) &
        Builders<SourceBuildJob>.Filter.OfType<SourceBuildExecution, ClaimedSourceBuildExecution>(
            item => item.Execution) &
        Builders<SourceBuildJob>.Filter.Eq(item => ((ClaimedSourceBuildExecution)item.Execution).WorkerId,
            identity.WorkerId) &
        Builders<SourceBuildJob>.Filter.Eq(item => ((ClaimedSourceBuildExecution)item.Execution).Attempt,
            identity.Attempt) &
        Builders<SourceBuildJob>.Filter.AfterServerNow(
            item => ((ClaimedSourceBuildExecution)item.Execution).ExpiresAtUtc);

    public static FilterDefinition<SourceBuildJob> LiveLease(SourceBuildJob job) =>
        job.Execution is ClaimedSourceBuildExecution lease
            ? OwnedLease(new(job.TenantId, job.Id, lease.WorkerId, lease.Attempt))
            : Builders<SourceBuildJob>.Filter.Where(_ => false);

    public static FilterDefinition<SourceBuildJob> Recoverable =>
        Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, QueuedSourceBuild>(item => item.State) |
        (Builders<SourceBuildJob>.Filter
            .OfType<SourceBuildExecution, ClaimedSourceBuildExecution>(item => item.Execution) &
            Builders<SourceBuildJob>.Filter.Not(SourceBuildJobFilters.TerminalStates()) &
            Builders<SourceBuildJob>.Filter.Not(
                Builders<SourceBuildJob>.Filter.OfType<SourceBuildJobState, DeployingSourceBuild>(item => item.State)) &
            Builders<SourceBuildJob>.Filter.AtOrBeforeServerNow(
                item => ((ClaimedSourceBuildExecution)item.Execution).ExpiresAtUtc));

    public static FilterDefinition<SourceBuildJob> RecoverableBefore(DateTime cutoffUtc) =>
        Recoverable & Builders<SourceBuildJob>.Filter.Lte(item => item.UpdatedAtUtc, cutoffUtc);

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

    public static FilterDefinition<SourceBuildJob> ActiveForWebService(
        string tenantId,
        string webServiceId) =>
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
            Builders<SourceBuildJob>.Filter.Eq(item => item.PromotionFence, job.PromotionFence),
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

    internal static FilterDefinition<SourceBuildJob> TerminalStates() =>
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

internal static class SourceBuildLogFilters
{
    public static FilterDefinition<SourceBuildLog> Owned(string tenantId, string webServiceId, string jobId) =>
        Builders<SourceBuildLog>.Filter.And(Builders<SourceBuildLog>.Filter.ForTenant(tenantId),
            Builders<SourceBuildLog>.Filter.Eq(log => log.WebServiceId, webServiceId), Builders<SourceBuildLog>.Filter.Eq(log => log.Id, jobId));
    public static FilterDefinition<SourceBuildLog> Writer(SourceBuildLogIdentity identity) =>
        Owned(identity.TenantId, identity.WebServiceId, identity.JobId) & Builders<SourceBuildLog>.Filter.Eq(log => log.WriterToken, identity.WriterToken);
    public static FilterDefinition<SourceBuildLog> AtRevision(SourceBuildLog log) =>
        Owned(log.TenantId, log.WebServiceId, log.Id) & Builders<SourceBuildLog>.Filter.Eq(value => value.Revision, log.Revision);
    public static FilterDefinition<SourceBuildLog> WritableAtRevision(SourceBuildLog log, DateTime now) =>
        AtRevision(log) & Builders<SourceBuildLog>.Filter.Eq(value => value.WriterToken, log.WriterToken) &
        Builders<SourceBuildLog>.Filter.OfType<SourceBuildLogState, OpenSourceBuildLog>(value => value.State) &
        Builders<SourceBuildLog>.Filter.Gt(value => ((OpenSourceBuildLog)value.State).LeaseExpiresAtUtc,
            now) & Builders<SourceBuildLog>.Filter.Gt(value => value.ExpiresAtUtc, now);
    public static FilterDefinition<SourceBuildLog> Recoverable(DateTime now) =>
        (Builders<SourceBuildLog>.Filter.Lte(log => log.ExpiresAtUtc, now) &
            Builders<SourceBuildLog>.Filter.Not(Builders<SourceBuildLog>.Filter.OfType<SourceBuildLogState, ExpiredSourceBuildLog>(value => value.State))) |
        (Builders<SourceBuildLog>.Filter.OfType<SourceBuildLogState, OpenSourceBuildLog>(value =>
            value.State) & Builders<SourceBuildLog>.Filter.Lte(value => ((
            OpenSourceBuildLog)value.State).LeaseExpiresAtUtc, now));
}

internal static class SourceBuildPromotionReceiptFilters
{
    public static FilterDefinition<SourceBuildPromotionReceipt> ByJob(SourceBuildPromotionRequest request) =>
        Builders<SourceBuildPromotionReceipt>.Filter.Eq(item => item.TenantId, request.TenantId) &
        Builders<SourceBuildPromotionReceipt>.Filter.Eq(item => item.Id, request.BuildJobId);
}
