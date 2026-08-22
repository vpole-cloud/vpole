using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence.Runtime.Gateway;
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

public sealed class FunctionRuntimeRouteResolver(
    LoadFunctionRuntimeRouteTarget loadRouteTarget)
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
        var target = await loadRouteTarget(normalizedRoute, cancellationToken);
        if (target is null)
            return null;

        var route = FunctionHttpRouteMatcher.Match(target.Binding.HttpRoutes, handlerPath, method, exposure);
        if (route is null)
            return null;
        return new ResolvedFunctionHttpRoute(target.App, target.Revision, route);
    }
}
