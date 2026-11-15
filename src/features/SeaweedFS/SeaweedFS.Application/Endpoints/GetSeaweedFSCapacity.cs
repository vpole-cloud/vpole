using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Services;
using Pole.Features.SeaweedFS.Domain;
using Pole.Features.Secrets.Contracts;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.SeaweedFS.Application.Endpoints;

public sealed record GetSeaweedFSCapacityOutcome(
    long StorageGb,
    long WorkloadInstances
);

public sealed record GetSeaweedFSCapacityRequest(
    [FromQuery] SeaweedFSMode Mode,
    [FromQuery] int SizeGb,
    [FromTenantHeader] string TenantId,
    [FromQuery] bool UsesExistingPostgres = false
) : IMediatorRequest<GetSeaweedFSCapacityOutcome>, ITenantReadOperation;

public sealed class GetSeaweedFSCapacityValidator : AbstractValidator<GetSeaweedFSCapacityRequest>
{
    public GetSeaweedFSCapacityValidator()
    {
        RuleFor(x => x.Mode).IsInEnum();
        RuleFor(x => x.SizeGb).InclusiveBetween(1, SeaweedFSStorage.MaximumSizeGb);
        RuleFor(x => x.UsesExistingPostgres)
            .Must((request, existing) => !existing || request.Mode == SeaweedFSMode.Replicated);
    }
}

public sealed class GetSeaweedFSCapacityHandler
    : IMediatorOutcomeHandler<GetSeaweedFSCapacityRequest, GetSeaweedFSCapacityOutcome>
{
    public Task<GetSeaweedFSCapacityOutcome> HandleAsync(
        GetSeaweedFSCapacityRequest request,
        CancellationToken cancellationToken = default)
    {
        var totals = SeaweedFSTopology.Calculate(request.Mode, 0, 0, request.SizeGb, request.UsesExistingPostgres);
        return Task.FromResult(new GetSeaweedFSCapacityOutcome(totals.StorageGb, totals.WorkloadInstances));
    }
}

public static class GetSeaweedFSCapacityEndpoint
{
    public static IEndpointRouteBuilder MapGetSeaweedFSCapacityEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/capacity",
                async (
                    IMediator mediator, [AsParameters] GetSeaweedFSCapacityRequest request,
                    CancellationToken ct) =>
                (await mediator.Send(request, ct)).ToHttpResult(value => Results.Ok(value)))
            .WithName("GetSeaweedFSCapacity");
        return app;
    }
}
