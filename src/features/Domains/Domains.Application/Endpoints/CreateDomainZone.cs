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
        app.MapPost("/", async (IMediator mediator, [AsParameters] CreateDomainZoneRequest request, CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken)).ToHttpResult(
                    ToHttpResult))
            .WithName("CreateDomainZone")
            .Produces<TenantDomainZoneSnapshot>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return app;
    }

    private static IResult ToHttpResult(CreateDomainZoneOutcome outcome) => outcome switch
    {
        TenantDomainZoneSnapshot zone =>
            Results.Created($"/api/v1/domain-zones/{zone.Id}", zone),
        DeploymentTargetDomainReservationAdmissionError error =>
            ToDeploymentTargetProblem(error),
        CreateManagedDomainZoneError error =>
            ToDomainZoneProblem(error)
    };

    private static IResult ToDeploymentTargetProblem(
        DeploymentTargetDomainReservationAdmissionError error) => error switch
    {
        InvalidDeploymentTargetId invalid => Problem(
            StatusCodes.Status400BadRequest,
            "deployment-target-id-invalid",
            "Deployment target ID is invalid",
            "deployment-targets.id.invalid",
            new Dictionary<string, object?>
            {
                ["deploymentTargetId"] = invalid.DeploymentTargetId
            }),
        DeploymentTargetNotFound notFound => Problem(
            StatusCodes.Status404NotFound,
            "deployment-target-not-found",
            "Deployment target not found",
            "deployment-targets.not-found",
            new Dictionary<string, object?>
            {
                ["deploymentTargetId"] = notFound.DeploymentTargetId
            }),
        DeploymentTargetDisabled disabled => Problem(
            StatusCodes.Status409Conflict,
            "deployment-target-disabled",
            "Deployment target is disabled",
            "deployment-targets.disabled",
            new Dictionary<string, object?>
            {
                ["deploymentTargetId"] = disabled.DeploymentTargetId
            }),
        DeploymentTargetDeletionRequested deleting => Problem(
            StatusCodes.Status409Conflict,
            "deployment-target-deletion-requested",
            "Deployment target is pending deletion",
            "deployment-targets.deletion-requested",
            new Dictionary<string, object?>
            {
                ["deploymentTargetId"] = deleting.DeploymentTargetId
            }),
        DeploymentTargetNewWorkloadsDisabled unavailable => Problem(
            StatusCodes.Status409Conflict,
            "deployment-target-new-workloads-disabled",
            "Deployment target does not accept new reservations",
            "deployment-targets.new-workloads-disabled",
            new Dictionary<string, object?>
            {
                ["deploymentTargetId"] = unavailable.DeploymentTargetId
            }),
        DeploymentTargetPublicIngressUnavailable unavailable => Problem(
            StatusCodes.Status422UnprocessableEntity,
            "deployment-target-public-ingress-unavailable",
            "Deployment target has no public ingress",
            "deployment-targets.public-ingress-unavailable",
            new Dictionary<string, object?>
            {
                ["deploymentTargetId"] = unavailable.DeploymentTargetId
            })
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
        string? detail = null)
    {
        extensions ??= new Dictionary<string, object?>();
        extensions["code"] = code;
        return Results.Problem(
            detail: detail,
            statusCode: status,
            title: title,
            type: $"https://pole.dev/problems/{type}",
            extensions: extensions);
    }
}

public sealed record CreateDomainZoneBody(string DeploymentTargetId, string Label);

public sealed record CreateDomainZoneRequest(
    [FromBody] CreateDomainZoneBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<CreateDomainZoneOutcome>, ITenantCreateOperation;

public union CreateDomainZoneOutcome(
    TenantDomainZoneSnapshot,
    DeploymentTargetDomainReservationAdmissionError,
    CreateManagedDomainZoneError);

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
    RunMongoOutcomeTransaction<CreateDomainZoneOutcome> runMongoTransaction,
    CreateManagedDomainZoneReservation createManagedDomainZone)
    : IMediatorOutcomeHandler<CreateDomainZoneRequest, CreateDomainZoneOutcome>
{
    public async Task<CreateDomainZoneOutcome> HandleAsync(
        CreateDomainZoneRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = await deploymentTargets.AdmitForDomainReservationAsync(
            request.TenantId,
            request.Body.DeploymentTargetId,
            cancellationToken);
        if (!target.TryGetValue(out var publicIngress, out var targetError))
            return targetError;

        return await runMongoTransaction(
            async (transaction, transactionCancellationToken) =>
            {
                var result = await createManagedDomainZone(
                    request.TenantId,
                    publicIngress.DeploymentTargetId,
                    request.Body.Label,
                    publicIngress.PublicBaseDomain,
                    publicIngress.WorkloadConfigurationRevision,
                    transaction.Session,
                    transactionCancellationToken);
                if (result.TryGetValue(out var zone, out var error))
                {
                    CreateDomainZoneOutcome outcome = zone;
                    return transaction.Commit(outcome);
                }

                CreateDomainZoneOutcome aborted = error;
                return transaction.Abort(aborted);
            },
            cancellationToken);
    }
}
