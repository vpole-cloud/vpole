using MongoDB.Driver;
using Pole.Features.Functions.Application;
using Pole.Features.Functions.Application.Declarations;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Runtime.Gateway;

public sealed record FunctionRuntimeRouteTarget(
    FunctionApp App,
    FunctionRevision Revision,
    FunctionRouteBinding Binding);

public delegate Task<FunctionRuntimeRouteTarget?> LoadFunctionRuntimeRouteTarget(
    string normalizedRoute,
    CancellationToken cancellationToken = default);

public sealed class MongoLoadFunctionRuntimeRouteTarget(
    IMongoCollection<FunctionApp> functionApps,
    IMongoCollection<FunctionRevision> functionRevisions,
    IMongoCollection<FunctionRouteBinding> functionRouteBindings)
{
    public async Task<FunctionRuntimeRouteTarget?> InvokeAsync(
        string normalizedRoute,
        CancellationToken cancellationToken = default)
    {
        var binding = await functionRouteBindings.Find(
                FunctionRuntimeRouteTargetFilters.ActiveBinding(normalizedRoute))
            .FirstOrDefaultAsync(cancellationToken);
        if (binding is null)
            return null;

        var revisionId = binding.ActiveRevisionId!;
        var app = await functionApps.Find(
                FunctionRuntimeRouteTargetFilters.ServingApp(binding, revisionId))
            .FirstOrDefaultAsync(cancellationToken);
        if (app is null)
            return null;

        var revision = await functionRevisions.Find(
                FunctionRuntimeRouteTargetFilters.ActiveRevision(binding, revisionId))
            .FirstOrDefaultAsync(cancellationToken);
        return revision is null ? null : new FunctionRuntimeRouteTarget(app, revision, binding);
    }
}
