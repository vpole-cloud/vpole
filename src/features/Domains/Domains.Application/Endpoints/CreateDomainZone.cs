using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Dns;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Application.Persistence;
using Pole.Database;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.Endpoints;

public static class CreateDomainZone
{
    public static IEndpointRouteBuilder MapCreateDomainZoneEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (IMediator mediator, [AsParameters] CreateDomainZoneRequest request, CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken)).ToHttpResult(
                    value => Results.Created($"/api/v1/domain-zones/{value.Id}", value)))
            .WithName("CreateDomainZone")
            .Produces<TenantDomainZoneSnapshot>(StatusCodes.Status201Created);
        return app;
    }
}

public sealed record CreateDomainZoneBody(string DeploymentTargetId, string Label);

public sealed record CreateDomainZoneRequest(
    [FromBody] CreateDomainZoneBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<TenantDomainZoneSnapshot>, ITenantCreateOperation;

public sealed class CreateDomainZoneRequestValidator : AbstractValidator<CreateDomainZoneRequest>
{
    public CreateDomainZoneRequestValidator()
    {
        RuleFor(request => request.Body).NotNull();
        RuleFor(request => request.TenantId)
            .MongoObjectId(TenantHeaders.TenantId);
        When(request => request.Body is not null, () =>
        {
            RuleFor(request => request.Body.DeploymentTargetId)
                .MongoObjectId("Deployment target ID");
            RuleFor(request => request.Body.Label)
                .Must(DnsLabel.IsValid)
                .WithMessage("Label must be one valid ASCII DNS label.")
                .Must(TenantDomainZoneLabelPolicy.IsManagedLabelAllowed)
                .WithMessage(
                    $"Labels beginning with '{TenantDomainZoneLabelPolicy.GeneratedPrefix}' are reserved for platform-generated zones.");
        });
    }
}

public sealed class CreateDomainZoneHandler(
    IDeploymentTargetLookup deploymentTargets,
    RunMongoTransaction<TenantDomainZoneSnapshot> runMongoTransaction,
    CreateManagedDomainZone createManagedDomainZone)
    : IMediatorHandler<CreateDomainZoneRequest, TenantDomainZoneSnapshot>
{
    public async Task<Result<TenantDomainZoneSnapshot>> HandleAsync(
        CreateDomainZoneRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = await deploymentTargets.ResolvePublicIngressForDomainReservationAsync(
            request.TenantId,
            request.Body.DeploymentTargetId,
            cancellationToken);
        if (!target.TryGetValue(out var publicIngress, out var failure))
            return failure;

        return await runMongoTransaction(
            (transaction, transactionCancellationToken) => createManagedDomainZone(
                request.TenantId,
                publicIngress.DeploymentTargetId,
                request.Body.Label,
                publicIngress.PublicBaseDomain,
                publicIngress.WorkloadConfigurationRevision,
                transaction,
                transactionCancellationToken),
            cancellationToken);
    }
}
