using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Runtime.Timers;

public sealed record FunctionInvocationDispatchSubject(
    FunctionApp App,
    FunctionRevision Revision);

public delegate Task<FunctionInvocationDispatchSubject?> LoadFunctionInvocationDispatchSubject(
    FunctionInvocation invocation,
    CancellationToken cancellationToken = default);

public sealed class MongoLoadFunctionInvocationDispatchSubject(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRevision> functionRevisions)
{
    public async Task<FunctionInvocationDispatchSubject?> InvokeAsync(
        FunctionInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var app = await functionApps.Find(
                FunctionInvocationDispatchSubjectFilters.EnabledApp(invocation))
            .FirstOrDefaultAsync(cancellationToken);
        if (app is null)
            return null;

        var revision = await functionRevisions.Find(
                FunctionInvocationDispatchSubjectFilters.DispatchableRevision(invocation))
            .FirstOrDefaultAsync(cancellationToken);
        return revision is null ? null : new FunctionInvocationDispatchSubject(app, revision);
    }
}

internal static class FunctionInvocationDispatchSubjectFilters
{
    public static FilterDefinition<FunctionApp> EnabledApp(FunctionInvocation invocation) =>
        Builders<FunctionApp>.Filter.And(
            Builders<FunctionApp>.Filter.Eq(app => app.TenantId, invocation.TenantId),
            Builders<FunctionApp>.Filter.Eq(app => app.Id, invocation.FunctionAppId),
            Builders<FunctionApp>.Filter.Eq(app => app.DesiredState, FunctionAppDesiredState.Enabled));

    public static FilterDefinition<FunctionRevision> DispatchableRevision(FunctionInvocation invocation) =>
        Builders<FunctionRevision>.Filter.And(
            Builders<FunctionRevision>.Filter.Eq(revision => revision.TenantId, invocation.TenantId),
            Builders<FunctionRevision>.Filter.Eq(revision => revision.FunctionAppId, invocation.FunctionAppId),
            Builders<FunctionRevision>.Filter.Eq(revision => revision.Id, invocation.RevisionId),
            Builders<FunctionRevision>.Filter.Or(
                Builders<FunctionRevision>.Filter
                    .OfType<FunctionRevisionStatus, ActiveFunctionRevisionStatus>(revision => revision.Status),
                Builders<FunctionRevision>.Filter
                    .OfType<FunctionRevisionStatus, DrainingFunctionRevisionStatus>(revision => revision.Status)));
}
