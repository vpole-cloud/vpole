using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;

namespace Pole.Features.Functions.Runtime.Gateway;

public sealed record ResolvedFunctionHttpRoute(
    FunctionApp App,
    FunctionRevision Revision,
    FunctionHttpRouteBinding Route);

public interface IFunctionRuntimeRouteResolver
{
    Task<ResolvedFunctionHttpRoute?> ResolveAsync(
        string functionRoute,
        string handlerPath,
        string method,
        FunctionExposure exposure,
        CancellationToken cancellationToken = default);
}

public sealed class FunctionRuntimeRouteResolver(IFunctionAppDbContext dbContext)
    : IFunctionRuntimeRouteResolver
{
    public async Task<ResolvedFunctionHttpRoute?> ResolveAsync(
        string functionRoute,
        string handlerPath,
        string method,
        FunctionExposure exposure,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoute = functionRoute.Trim().ToLowerInvariant();
        if (normalizedRoute.Length == 0)
            return null;

        // Promotion updates the binding, app and revision atomically. A retry avoids a
        // transient miss if a read happens while the transaction is becoming visible.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var resolved = await TryResolveAsync(
                normalizedRoute,
                handlerPath,
                method,
                exposure,
                cancellationToken);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    private async Task<ResolvedFunctionHttpRoute?> TryResolveAsync(
        string normalizedRoute,
        string handlerPath,
        string method,
        FunctionExposure exposure,
        CancellationToken cancellationToken)
    {
        var binding = await dbContext.FunctionRouteBindings.Find(candidate =>
                candidate.NormalizedRoute == normalizedRoute &&
                candidate.State == FunctionRouteBindingState.Active &&
                candidate.ActiveRevisionId != null)
            .FirstOrDefaultAsync(cancellationToken);
        if (binding is null)
            return null;

        var route = FunctionHttpRouteMatcher.Match(binding.HttpRoutes, handlerPath, method, exposure);
        if (route is null)
            return null;

        var revisionId = binding.ActiveRevisionId!;
        var app = await dbContext.FunctionApps.Find(candidate =>
                candidate.TenantId == binding.TenantId &&
                candidate.Id == binding.FunctionAppId &&
                candidate.FunctionRoute == binding.NormalizedRoute &&
                candidate.ActiveRevisionId == revisionId &&
                candidate.DesiredState == FunctionAppDesiredState.Enabled &&
                candidate.Status == FunctionAppStatus.Ready)
            .FirstOrDefaultAsync(cancellationToken);
        if (app is null)
            return null;

        var revision = await dbContext.FunctionRevisions.Find(
                Builders<FunctionRevision>.Filter.And(
                    Builders<FunctionRevision>.Filter.Eq(candidate => candidate.TenantId, binding.TenantId),
                    Builders<FunctionRevision>.Filter.Eq(candidate => candidate.FunctionAppId, binding.FunctionAppId),
                    Builders<FunctionRevision>.Filter.Eq(candidate => candidate.Id, revisionId),
                    Builders<FunctionRevision>.Filter
                        .OfType<FunctionRevisionStatus, ActiveFunctionRevisionStatus>(candidate => candidate.Status)))
            .FirstOrDefaultAsync(cancellationToken);
        return revision is null
            ? null
            : new ResolvedFunctionHttpRoute(app, revision, route);
    }
}
