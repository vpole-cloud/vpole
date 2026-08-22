using MongoDB.Driver;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Application.Persistence.Runtime.Gateway;

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

internal static class FunctionRuntimeRouteTargetFilters
{
    public static FilterDefinition<FunctionRouteBinding> ActiveBinding(string normalizedRoute) =>
        Builders<FunctionRouteBinding>.Filter.And(
            Builders<FunctionRouteBinding>.Filter.Eq(binding => binding.NormalizedRoute, normalizedRoute),
            Builders<FunctionRouteBinding>.Filter.Eq(binding => binding.State, FunctionRouteBindingState.Active),
            Builders<FunctionRouteBinding>.Filter.Ne(binding => binding.ActiveRevisionId, null));

    public static FilterDefinition<FunctionApp> ServingApp(
        FunctionRouteBinding binding,
        string revisionId) =>
        Builders<FunctionApp>.Filter.And(
            Builders<FunctionApp>.Filter.Eq(app => app.TenantId, binding.TenantId),
            Builders<FunctionApp>.Filter.Eq(app => app.Id, binding.FunctionAppId),
            Builders<FunctionApp>.Filter.Eq(app => app.FunctionRoute, binding.NormalizedRoute),
            Builders<FunctionApp>.Filter.Eq(app => app.ActiveRevisionId, revisionId),
            Builders<FunctionApp>.Filter.Eq(app => app.DesiredState, FunctionAppDesiredState.Enabled),
            Builders<FunctionApp>.Filter.Eq(app => app.Status, FunctionAppStatus.Ready));

    public static FilterDefinition<FunctionRevision> ActiveRevision(
        FunctionRouteBinding binding,
        string revisionId) =>
        Builders<FunctionRevision>.Filter.And(
            Builders<FunctionRevision>.Filter.Eq(revision => revision.TenantId, binding.TenantId),
            Builders<FunctionRevision>.Filter.Eq(revision => revision.FunctionAppId, binding.FunctionAppId),
            Builders<FunctionRevision>.Filter.Eq(revision => revision.Id, revisionId),
            Builders<FunctionRevision>.Filter
                .OfType<FunctionRevisionStatus, ActiveFunctionRevisionStatus>(revision => revision.Status));
}
