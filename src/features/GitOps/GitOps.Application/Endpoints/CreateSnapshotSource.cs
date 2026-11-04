using Pole.Features.GitOps.Domain;
using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Mediator;

namespace Pole.Features.GitOps.Application.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateSnapshotSourceBody(
    Guid IdempotencyKey,
    string SourceUrl);

public sealed record CreateSnapshotSourceRequest(
    [FromBody] CreateSnapshotSourceBody Body,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<CreateSnapshotSourceOutcome>, ITenantCreateOperation;

public union CreateSnapshotSourceOutcome(
    SnapshotSourceStatus,
    SnapshotSourceRelocationRequired);

public sealed class CreateSnapshotSourceValidator : AbstractValidator<CreateSnapshotSourceRequest>
{
    public CreateSnapshotSourceValidator()
    {
        RuleFor(item => item.Body).NotNull();
        RuleFor(item => item.Body.IdempotencyKey).NotEmpty().When(item => item.Body is not null);
        RuleFor(item => item.Body.SourceUrl).Must(ManualSnapshotSources.ValidUrl).When(item => item.Body is not null);
    }
}

public sealed class CreateSnapshotSourceHandler(ManualSnapshotSources sources)
    : IMediatorOutcomeHandler<CreateSnapshotSourceRequest, CreateSnapshotSourceOutcome>
{
    public async Task<CreateSnapshotSourceOutcome> HandleAsync(
        CreateSnapshotSourceRequest request,
        CancellationToken cancellationToken = default)
    {
        var created = await sources.CreateAsync(
            request.TenantId,
            request.Body.IdempotencyKey,
            request.Body.SourceUrl,
            cancellationToken);

        return created switch
        {
            ManualSnapshotSource source => await sources.StatusAsync(source, cancellationToken),
            SnapshotSourceRelocationRequired error => new CreateSnapshotSourceOutcome(error)
        };
    }
}

public static class CreateSnapshotSourceEndpoint
{
    public static IEndpointRouteBuilder MapCreateSnapshotSourceEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (
                IMediator mediator,
                [AsParameters] CreateSnapshotSourceRequest request,
                CancellationToken ct) =>
            (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    SnapshotSourceStatus source => Results.Created($"/api/v1/gitops/sources/{source.Id}", source),
                    SnapshotSourceRelocationRequired error => SnapshotHttpMappings.Map(error)
                })).WithName("CreateManualSnapshotSource")
            .WithMetadata(new RequestSizeLimitAttribute(SnapshotHttpLimits.MaximumSourceRequestBytes));

        return app;
    }
}
