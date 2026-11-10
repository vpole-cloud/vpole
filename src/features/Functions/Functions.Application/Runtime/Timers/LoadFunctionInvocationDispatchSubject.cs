using MongoDB.Driver;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Runtime.Timers;

public sealed record FunctionInvocationDispatchSubject(
    FunctionApp App,
    FunctionRevision Revision
);

public delegate Task<FunctionInvocationDispatchSubject?> LoadFunctionInvocationDispatchSubject(
    FunctionInvocation invocation,
    CancellationToken cancellationToken = default);

public sealed class MongoLoadFunctionInvocationDispatchSubject(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRevision> functionRevisions
)
{
    public async Task<FunctionInvocationDispatchSubject?> InvokeAsync(
        FunctionInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var app = await functionApps.Find(FunctionInvocationDispatchSubjectFilters.EnabledApp(invocation))
            .FirstOrDefaultAsync(cancellationToken);
        if (app is null)
            return null;

        var revision = await functionRevisions.Find(
                FunctionInvocationDispatchSubjectFilters.DispatchableRevision(invocation))
            .FirstOrDefaultAsync(cancellationToken);
        return revision is null ? null : new FunctionInvocationDispatchSubject(app, revision);
    }
}
