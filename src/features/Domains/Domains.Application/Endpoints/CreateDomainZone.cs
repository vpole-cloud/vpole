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
        CreateDomainZoneError error => ToProblem(error, httpProblems)
    };

    private static IResult ToProblem(
        CreateDomainZoneError error,
        IHttpProblemMapper httpProblems) => error switch
    {
        DeploymentTargetDomainReservationAdmissionError deploymentTargetError =>
            httpProblems.Map(deploymentTargetError).ToHttpResult(),
        CreateManagedDomainZoneError domainZoneError =>
            ToDomainZoneProblem(domainZoneError)
    };

    private static IResult ToDomainZoneProblem(CreateManagedDomainZoneError error) =>
        error switch
        {
            InvalidManagedDomainZone invalid => Problem(
                StatusCodes.Status422UnprocessableEntity,
                "domain-zone-invalid",
                "Domain zone is invalid",
                "domains.zone.invalid",
                detail: invalid.Message),
            ManagedDomainZoneAlreadyReserved reserved => Problem(
                StatusCodes.Status409Conflict,
                "domain-zone-already-reserved",
                "Domain zone is already reserved",
                "domains.zone.already-reserved",
                new Dictionary<string, object?>
                {
                    ["domain"] = reserved.Domain
                }),
            ManagedDomainZoneDeploymentTargetChanged changed => Problem(
                StatusCodes.Status409Conflict,
                "domain-zone-deployment-target-changed",
                "Deployment target changed during reservation",
                "domains.zone.deployment-target-changed",
                new Dictionary<string, object?>
                {
                    ["deploymentTargetId"] = changed.DeploymentTargetId
                }),
            ManagedDomainZoneReferenceRejected rejected => Problem(
                StatusCodes.Status409Conflict,
                "domain-zone-reference-rejected",
                "Deployment target rejected the domain reservation",
                "domains.zone.deployment-target-reference-rejected",
                new Dictionary<string, object?>
                {
                    ["deploymentTargetId"] = rejected.DeploymentTargetId
                })
        };

    private static IResult Problem(
        int status,
        string type,
        string title,
        string code,
        IDictionary<string, object?>? extensions = null,
        string? detail = null) =>
        new HttpProblem(
                status,
                $"https://pole.dev/problems/{type}",
                title,
                code,
                detail,
                extensions is null
                    ? null
                    : new Dictionary<string, object?>(extensions))
            .ToHttpResult();
}

public sealed record CreateDomainZoneBody(string DeploymentTargetId, string Label);

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
    IDeploymentTargetPublicIngressAdmission deploymentTargets,
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
        var result = await deploymentTargets
            .AdmitForDomainReservationAsync(
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
