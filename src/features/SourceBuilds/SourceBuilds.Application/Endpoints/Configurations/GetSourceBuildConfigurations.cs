// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Configurations;
using Pole.Mediator;

namespace Pole.Features.SourceBuilds.Application.Endpoints.Configurations;

public sealed record GetSourceBuildConfigurationsRequest(
    [FromTenantHeader] string TenantId
) : IMediatorRequest<GetSourceBuildConfigurationsOutcome>, ITenantReadOperation;

public union GetSourceBuildConfigurationsOutcome(IReadOnlyList<SourceBuildConfigurationDto>);

public sealed class GetSourceBuildConfigurationsHandler(
    LoadSourceBuildConfigurations loadSourceBuildConfigurations,
    IGitOpsMutations git
) : IMediatorOutcomeHandler<GetSourceBuildConfigurationsRequest, GetSourceBuildConfigurationsOutcome>
{
    public async Task<GetSourceBuildConfigurationsOutcome> HandleAsync(
        GetSourceBuildConfigurationsRequest request,
        CancellationToken cancellationToken = default)
    {
        var configurations = await loadSourceBuildConfigurations(request.TenantId, cancellationToken);
        var ownership = await git.StatusesAsync(
            new LoadGitResourceStatusesRequest(
                request.TenantId,
                "SourceBuildConfiguration",
                configurations.Select(item => item.Id).ToArray()),
            cancellationToken);
        return configurations.Select(item => item.ToDto(ownership.GetValueOrDefault(item.Id))).ToArray();
    }
}

public static class GetSourceBuildConfigurationsEndpoint
{
    public static IEndpointRouteBuilder MapGetSourceBuildConfigurationsEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/",
                async (
                    IMediator mediator, [AsParameters] GetSourceBuildConfigurationsRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        IReadOnlyList<SourceBuildConfigurationDto> configurations => Results.Ok(configurations),
                    });
                })
            .WithName("GetSourceBuildConfigurations")
            .Produces<IReadOnlyList<SourceBuildConfigurationDto>>();
        return app;
    }
}
