using FluentValidation;
using Pole.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Application.Persistence.Mutations;
using Pole.Features.DeploymentTargets.Application.Persistence.Reads;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Mediator;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record ValidateDeploymentTargetRequest(
    [FromRoute] string Id)
    : IMediatorRequest<ValidateDeploymentTargetOutcome>;

public union ValidateDeploymentTargetOutcome(
    DeploymentTargetDto,
    DeploymentTargetNotFound,
    DeploymentTargetRevisionConflict);

public sealed class ValidateDeploymentTargetHandler(
    LoadDeploymentTargetById loadDeploymentTargetById,
    LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId,
    PersistDeploymentTargetValidation persistDeploymentTargetValidation,
    DeploymentTargetValidationService validationService)
    : IMediatorOutcomeHandler<ValidateDeploymentTargetRequest, ValidateDeploymentTargetOutcome>
{
    public async Task<ValidateDeploymentTargetOutcome> HandleAsync(
        ValidateDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = await loadDeploymentTargetById(request.Id, cancellationToken);
        if (target is null)
            return new DeploymentTargetNotFound(request.Id);

        var observation = await validationService.ProbeStoredConnectionAsync(
            target,
            cancellationToken);

        var updated = target.ApplyValidationObservation(
            target.Revision,
            target.Connection.Version,
            observation.Health,
            observation.Capabilities);
        var persistedResult = await persistDeploymentTargetValidation(
            target,
            updated,
            cancellationToken);
        if (persistedResult is DeploymentTargetRevisionConflict persistenceError)
            return persistenceError;

        var persisted = (DeploymentTarget)persistedResult.Value;
        var defaultId = await loadDefaultDeploymentTargetId(cancellationToken);
        return persisted.ToDto(persisted.Id == defaultId);
    }
}

public sealed class ValidateDeploymentTargetRequestValidator
    : AbstractValidator<ValidateDeploymentTargetRequest>
{
    public ValidateDeploymentTargetRequestValidator() => RuleFor(request => request.Id)
            .MongoObjectId("Deployment target ID");
}

public static class ValidateDeploymentTargetEndpoint
{
    public static IEndpointRouteBuilder MapValidateDeploymentTargetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/validate", async (
                IMediator mediator,
                [AsParameters] ValidateDeploymentTargetRequest request,
                CancellationToken cancellationToken) => {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    DeploymentTargetDto target => Results.Ok(target),
                    DeploymentTargetNotFound error => DeploymentTargetHttpMappings.Map(error),
                    DeploymentTargetRevisionConflict error => DeploymentTargetHttpMappings.Map(error)
                });
            })
            .WithName("ValidateDeploymentTarget")
            .Produces<DeploymentTargetDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
