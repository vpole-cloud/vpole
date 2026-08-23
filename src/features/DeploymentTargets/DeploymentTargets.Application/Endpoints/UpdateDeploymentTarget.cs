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
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record UpdateDeploymentTargetRequestBody(
    string DisplayName,
    bool Enabled,
    bool AcceptsWorkloads,
    DeploymentTargetWorkloadConfigurationRequestBody WorkloadConfiguration,
    long Revision);

public sealed record UpdateDeploymentTargetRequest(
    [FromRoute] string Id,
    [FromBody] UpdateDeploymentTargetRequestBody Body)
    : IMediatorRequest<UpdateDeploymentTargetOutcome>;

public union UpdateDeploymentTargetError(
    DeploymentTargetNotFound,
    DeploymentTargetRevisionConflict,
    DeploymentTargetDeletionRejected,
    PersistUpdatedDeploymentTargetError);

public union UpdateDeploymentTargetOutcome(
    DeploymentTargetDto,
    UpdateDeploymentTargetError);

public sealed class UpdateDeploymentTargetHandler(
    LoadDeploymentTargetById loadDeploymentTargetById,
    LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId,
    PersistUpdatedDeploymentTarget persistUpdatedDeploymentTarget,
    InvalidateDeploymentTargetKubernetesClient invalidateDeploymentTargetKubernetesClient,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<UpdateDeploymentTargetRequest, UpdateDeploymentTargetOutcome>
{
    public async Task<UpdateDeploymentTargetOutcome> HandleAsync(
        UpdateDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = await loadDeploymentTargetById(request.Id, cancellationToken);
        if (target is null)
            return Error(new DeploymentTargetNotFound(request.Id));
        if (target.Revision != request.Body.Revision)
            return Error(new DeploymentTargetRevisionConflict(target.Id));
        if (request.Body.AcceptsWorkloads && target.DeletionRequestedAtUtc is not null)
            return Error(new DeploymentTargetDeletionRejected(target.Id));

        var updated = target.Reconfigure(
            request.Body.DisplayName,
            request.Body.Enabled,
            request.Body.AcceptsWorkloads,
            request.Body.WorkloadConfiguration.Ingress?.ToDomain(),
            DeploymentTargetClock.UtcNowAfter(timeProvider, target.UpdatedAtUtc));

        if (updated.Revision == target.Revision)
        {
            var existingDefaultId = await loadDefaultDeploymentTargetId(cancellationToken);
            return target.ToDto(target.Id == existingDefaultId);
        }

        var persistedResult = await persistUpdatedDeploymentTarget(
            target,
            updated,
            cancellationToken);
        if (!persistedResult.TryGetValue(out var persisted, out var persistenceError))
            return Error(persistenceError);

        if (!persisted.Enabled)
            invalidateDeploymentTargetKubernetesClient(target.Id);
        var defaultId = await loadDefaultDeploymentTargetId(cancellationToken);
        return persisted.ToDto(persisted.Id == defaultId);
    }

    private static UpdateDeploymentTargetOutcome Error(DeploymentTargetNotFound error) =>
        new(new UpdateDeploymentTargetError(error));

    private static UpdateDeploymentTargetOutcome Error(DeploymentTargetRevisionConflict error) =>
        new(new UpdateDeploymentTargetError(error));

    private static UpdateDeploymentTargetOutcome Error(DeploymentTargetDeletionRejected error) =>
        new(new UpdateDeploymentTargetError(error));

    private static UpdateDeploymentTargetOutcome Error(PersistUpdatedDeploymentTargetError error) =>
        new(new UpdateDeploymentTargetError(error));
}

public sealed class UpdateDeploymentTargetRequestValidator
    : AbstractValidator<UpdateDeploymentTargetRequest>
{
    public UpdateDeploymentTargetRequestValidator()
    {
        RuleFor(request => request.Id)
            .MongoObjectId("Deployment target ID");
        RuleFor(request => request.Body)
            .NotNull()
            .SetValidator(new UpdateDeploymentTargetRequestBodyValidator());
    }
}

public sealed class UpdateDeploymentTargetRequestBodyValidator
    : AbstractValidator<UpdateDeploymentTargetRequestBody>
{
    public UpdateDeploymentTargetRequestBodyValidator()
    {
        RuleFor(body => body.DisplayName)
            .NotEmpty()
            .MaximumLength(80);
        RuleFor(body => body.Revision).GreaterThan(0);
        RuleFor(body => body.AcceptsWorkloads)
            .Equal(false)
            .When(body => !body.Enabled)
            .WithMessage("A disabled deployment target cannot accept workloads.");
        RuleFor(body => body.WorkloadConfiguration)
            .NotNull()
            .SetValidator(new DeploymentTargetWorkloadConfigurationRequestBodyValidator());
    }
}

public static class UpdateDeploymentTargetEndpoint
{
    public static IEndpointRouteBuilder MapUpdateDeploymentTargetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPut("/{Id}", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] UpdateDeploymentTargetRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    DeploymentTargetDto target => Results.Ok(target),
                    UpdateDeploymentTargetError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("UpdateDeploymentTarget")
            .Produces<DeploymentTargetDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
