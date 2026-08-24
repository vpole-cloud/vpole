using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Persistence;
using Pole.Mediator;

namespace Pole.Features.SourceBuilds.Application.Endpoints;

public sealed record GetSourceBuildConfigurationsRequest(
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<GetSourceBuildConfigurationsOutcome>, ITenantReadOperation;

public union GetSourceBuildConfigurationsOutcome(IReadOnlyList<SourceBuildConfigurationDto>);

public sealed class GetSourceBuildConfigurationsHandler(
    LoadSourceBuildConfigurations loadSourceBuildConfigurations)
    : IMediatorOutcomeHandler<
        GetSourceBuildConfigurationsRequest,
        GetSourceBuildConfigurationsOutcome>
{
    public async Task<GetSourceBuildConfigurationsOutcome> HandleAsync(
        GetSourceBuildConfigurationsRequest request,
        CancellationToken cancellationToken = default)
    {
        var configurations = await loadSourceBuildConfigurations(
            request.TenantId,
            cancellationToken);
        return configurations.Select(item => item.ToDto()).ToArray();
    }
}

public static class GetSourceBuildConfigurationsEndpoint
{
    public static IEndpointRouteBuilder MapGetSourceBuildConfigurationsEndpoint(
        this IEndpointRouteBuilder app)
    {
        app.MapGet("/", async (
                IMediator mediator,
                [AsParameters] GetSourceBuildConfigurationsRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    IReadOnlyList<SourceBuildConfigurationDto> configurations => Results.Ok(configurations)
                });
            })
            .WithName("GetSourceBuildConfigurations")
            .Produces<IReadOnlyList<SourceBuildConfigurationDto>>();
        return app;
    }
}
