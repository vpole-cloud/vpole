using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.GitOps.Application.Endpoints;

public sealed record GetSnapshotSourceRequest(
    [FromRoute] string Id,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<GetSnapshotSourceOutcome>, ITenantReadOperation;
public union GetSnapshotSourceOutcome(
    SnapshotSourceStatus,
    SnapshotSourceMissing);
public sealed class GetSnapshotSourceValidator : AbstractValidator<GetSnapshotSourceRequest>
{
    public GetSnapshotSourceValidator() => RuleFor(item => item.Id).MongoObjectId("Source ID");
}
public sealed class GetSnapshotSourceHandler(ManualSnapshotSources sources) : IMediatorOutcomeHandler<GetSnapshotSourceRequest, GetSnapshotSourceOutcome>
{
    public async Task<GetSnapshotSourceOutcome> HandleAsync(GetSnapshotSourceRequest request, CancellationToken cancellationToken = default)
    {
        var source = await sources.LoadAsync(request.TenantId, request.Id, cancellationToken);
        return source is null ? new SnapshotSourceMissing() : await sources.StatusAsync(source, cancellationToken);
    }
}
public static class GetSnapshotSourceEndpoint
{
    public static IEndpointRouteBuilder MapGetSnapshotSourceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/{Id}", async (IMediator mediator, [AsParameters] GetSnapshotSourceRequest request, CancellationToken ct) =>
            (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
            {
                SnapshotSourceStatus source => Results.Ok(source),
                SnapshotSourceMissing error => SnapshotHttpMappings.Map(error)
            })).WithName("GetManualSnapshotSource");
        return app;
    }
}
