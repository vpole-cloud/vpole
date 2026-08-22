using MongoDB.Driver;
using Pole.Features.Functions.Application.Persistence;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Reads;

public delegate Task<IReadOnlyList<FunctionInvocation>> LoadFunctionInvocations(
    FunctionApp functionApp,
    int take,
    CancellationToken cancellationToken = default);

internal sealed class MongoLoadFunctionInvocations(
    IMongoCollection<FunctionInvocation> functionInvocations)
{
    public async Task<IReadOnlyList<FunctionInvocation>> InvokeAsync(
        FunctionApp functionApp,
        int take,
        CancellationToken cancellationToken = default) =>
        await functionInvocations
            .Find(FunctionInvocationFilters.ByFunctionApp(functionApp.TenantId, functionApp.Id))
            .SortByDescending(invocation => invocation.CreatedAtUtc)
            .Limit(take)
            .ToListAsync(cancellationToken);
}
