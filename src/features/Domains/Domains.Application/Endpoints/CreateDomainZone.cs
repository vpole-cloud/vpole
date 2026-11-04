using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Dns;
using Pole.Workloads;
using MongoDB.Bson;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Domains.Application.Persistence;
using Pole.Features.Domains.Application.Persistence.DomainZones;
using Pole.Features.Domains.Contracts;
using Pole.Features.Domains.Domain;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;
using static Pole.Features.DeploymentTargets.Contracts.HttpMapping.DeploymentTargetContractHttpMappings;
using static Pole.Features.Domains.Application.Common.DomainHttpMappings;

namespace Pole.Features.Domains.Application.Endpoints;

public static class CreateDomainZone
{
    public static IEndpointRouteBuilder MapCreateDomainZoneEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (IMediator mediator, [AsParameters] CreateDomainZoneRequest request, CancellationToken cancellationToken) =>
                (await mediator.Send(request, cancellationToken)).ToHttpResult(
                    ToHttpResult))
            .WithName("CreateDomainZone")
            .Produces<DomainZoneDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return app;
    }

    private static IResult ToHttpResult(
        CreateDomainZoneOutcome outcome) => outcome switch
        {
            TenantDomainZoneSnapshot zone =>
                Results.Created($"/api/v1/domain-zones/{zone.Id}", DomainZoneDto.FromSnapshot(zone, null)),
            DeploymentTargetDomainReservationAdmissionError error => MapDomainReservationAdmissionError(error),
            ManagedDomainZoneAlreadyReserved error => MapManagedDomainZoneAlreadyReserved(error),
            ManagedDomainZoneNameConflict error => MapManagedDomainZoneNameConflict(error),
            ManagedDomainZoneDeploymentTargetChanged error => MapManagedDomainZoneDeploymentTargetChanged(error),
            ManagedDomainZoneReferenceRejected error => MapManagedDomainZoneReferenceRejected(error)
        };
}

public sealed record CreateDomainZoneBody(
    string DeploymentTargetId,
    string Label,
    string TechnicalName);

public sealed record CreateDomainZoneRequest(
    [FromBody] CreateDomainZoneBody Body,
    [FromTenantHeader] string TenantId)
    : IMediatorRequest<CreateDomainZoneOutcome>, ITenantCreateOperation;

public union CreateDomainZoneOutcome(
    TenantDomainZoneSnapshot,
    DeploymentTargetDomainReservationAdmissionError,
    ManagedDomainZoneAlreadyReserved,
    ManagedDomainZoneNameConflict,
    ManagedDomainZoneDeploymentTargetChanged,
    ManagedDomainZoneReferenceRejected);

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
            RuleFor(request => request.Body.TechnicalName)
                .Must(ResourceTechnicalName.IsValid)
                .WithMessage("Technical name must be a lowercase DNS label beginning with a letter.");
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
    RunMongoOutcomeTransaction<CreateDomainZoneOutcome> runMongoTransaction,
    CreateManagedDomainZoneReservation createManagedDomainZone)
    : IMediatorOutcomeHandler<CreateDomainZoneRequest, CreateDomainZoneOutcome>
{
    public async Task<CreateDomainZoneOutcome> HandleAsync(
        CreateDomainZoneRequest request,
        CancellationToken cancellationToken = default)
    {
        var admission = await admitPublicIngress(
            request.TenantId,
            request.Body.DeploymentTargetId,
            cancellationToken);
        if (admission is DeploymentTargetDomainReservationAdmissionError admissionError)
            return admissionError;
        var publicIngress = (DeploymentTargetPublicIngressDto)admission.Value;

        return await runMongoTransaction(async (transaction, ct) =>
        {
            var reserved = await createManagedDomainZone(
                new CreateManagedDomainZoneCommand(
                    ObjectId.GenerateNewId().ToString(),
                    request.TenantId,
                    publicIngress.DeploymentTargetId,
                    request.Body.TechnicalName,
                    request.Body.Label,
                    publicIngress.PublicBaseDomain,
                    publicIngress.WorkloadConfigurationRevision),
                transaction.Session,
                ct);
            return reserved switch
            {
                TenantDomainZoneSnapshot zone => transaction.Commit(zone),
                ManagedDomainZoneAlreadyReserved error => transaction.Abort(error),
                ManagedDomainZoneNameConflict error => transaction.Abort(error),
                ManagedDomainZoneDeploymentTargetChanged error => transaction.Abort(error),
                ManagedDomainZoneReferenceRejected error => transaction.Abort(error)
            };
        }, cancellationToken);
    }
}
