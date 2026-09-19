using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Dns;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Application.Persistence;
using Pole.Database;
using Pole.Features.Domains.Application.Persistence.DomainZones;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;
using Pole.Features.Domains.Domain;

namespace Pole.Features.Domains.Application.Endpoints;

public static class CreateDomainZone
{
    public static IEndpointRouteBuilder MapCreateDomainZoneEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (IMediator mediator, IHttpProblemMapper httpProblems, [AsParameters] CreateDomainZoneRequest request, CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken)).ToHttpResult(
                    outcome => ToHttpResult(outcome, httpProblems)))
            .WithName("CreateDomainZone")
            .Produces<TenantDomainZoneSnapshot>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return app;
    }

    private static IResult ToHttpResult(
        CreateDomainZoneOutcome outcome,
        IHttpProblemMapper httpProblems) => outcome switch
        {
            TenantDomainZoneSnapshot zone =>
                Results.Created($"/api/v1/domain-zones/{zone.Id}", zone),
            CreateDomainZoneError error => httpProblems.Map(error).ToHttpResult()
        };
}

public sealed record CreateDomainZoneBody(
    string DeploymentTargetId,
    string Label);

public sealed record CreateDomainZoneRequest(
    [FromBody] CreateDomainZoneBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<CreateDomainZoneOutcome>, ITenantCreateOperation;

public union CreateDomainZoneError(
    DeploymentTargetDomainReservationAdmissionError,
    CreateManagedDomainZoneError);

public union CreateDomainZoneOutcome(
    TenantDomainZoneSnapshot,
    CreateDomainZoneError);

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
    AdmitDeploymentTargetPublicIngressForDomainReservation admitPublicIngress,
    RunMongoOutcomeTransaction<Result<
        TenantDomainZoneSnapshot,
        CreateDomainZoneError>> runMongoTransaction,
    CreateManagedDomainZoneReservation createManagedDomainZone)
    : IMediatorOutcomeHandler<CreateDomainZoneRequest, CreateDomainZoneOutcome>
{
    public async Task<CreateDomainZoneOutcome> HandleAsync(
        CreateDomainZoneRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await admitPublicIngress(
                request.TenantId,
                request.Body.DeploymentTargetId,
                cancellationToken)
            .MapErrorAsync(CreateDomainZoneError (error) => error)
            .ThenAsync(publicIngress => ReserveAsync(
                request,
                publicIngress,
                cancellationToken));

        return result.Match(
            CreateDomainZoneOutcome (zone) => zone,
            CreateDomainZoneOutcome (error) => error);
    }

    private Task<Result<TenantDomainZoneSnapshot, CreateDomainZoneError>> ReserveAsync(
        CreateDomainZoneRequest request,
        DeploymentTargetPublicIngressDto publicIngress,
        CancellationToken cancellationToken) =>
        runMongoTransaction(
            async (transaction, transactionCancellationToken) =>
            {
                var result = (await createManagedDomainZone(
                        request.TenantId,
                        publicIngress.DeploymentTargetId,
                        request.Body.Label,
                        publicIngress.PublicBaseDomain,
                        publicIngress.WorkloadConfigurationRevision,
                        transaction.Session,
                        transactionCancellationToken))
                    .MapError(CreateDomainZoneError (error) => error);

                return result.Match(
                    zone => transaction.Commit(new Ok<TenantDomainZoneSnapshot>(zone)),
                    error => transaction.Abort(new Err<CreateDomainZoneError>(error)));
            },
            cancellationToken);
}
