using System.Text.Json.Serialization;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Catalog;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Mediator;
using Pole.Validation;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateDeploymentTargetRequestBody(
    string DisplayName,
    bool Enabled,
    bool AcceptsWorkloads,
    DeploymentTargetWorkloadConfigurationRequestBody WorkloadConfiguration,
    long Revision
);

public sealed record UpdateDeploymentTargetRequest(
    [FromRoute] string Id,
    [FromBody] UpdateDeploymentTargetRequestBody Body
) : IMediatorRequest<UpdateDeploymentTargetOutcome>;

public union UpdateDeploymentTargetOutcome(
    DeploymentTargetDto,
    DeploymentTargetNotFound,
    DeploymentTargetRevisionConflict,
    DeploymentTargetDeletionRejected,
    DeploymentTargetPublicBaseDomainReferenced,
    DeploymentTargetPublicBaseDomainConflict);

public sealed class UpdateDeploymentTargetHandler(
    LoadDeploymentTargetById loadDeploymentTargetById,
    LoadDefaultDeploymentTargetId loadDefaultDeploymentTargetId,
    PersistUpdatedDeploymentTarget persistUpdatedDeploymentTarget,
    InvalidateDeploymentTargetKubernetesClient invalidateDeploymentTargetKubernetesClient,
    TimeProvider timeProvider
) : IMediatorOutcomeHandler<UpdateDeploymentTargetRequest, UpdateDeploymentTargetOutcome>
{
    public async Task<UpdateDeploymentTargetOutcome> HandleAsync(
        UpdateDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = await loadDeploymentTargetById(request.Id, cancellationToken);
        if (target is null)
            return new DeploymentTargetNotFound(request.Id);
        if (target.Revision != request.Body.Revision)
            return new DeploymentTargetRevisionConflict(target.Id);
        if (request.Body.AcceptsWorkloads && target.DeletionRequestedAtUtc is not null)
            return new DeploymentTargetDeletionRejected(target.Id);

        var updated = target.Reconfigure(
            request.Body.DisplayName,
            request.Body.Enabled,
            request.Body.AcceptsWorkloads,
            request.Body.WorkloadConfiguration.Ingress?.ToDomain(),
            request.Body.WorkloadConfiguration.NetworkSources.ToDomain(),
            DeploymentTargetClock.UtcNowAfter(timeProvider, target.UpdatedAtUtc));

        if (updated.Revision == target.Revision)
        {
            var existingDefaultId = await loadDefaultDeploymentTargetId(cancellationToken);
            return target.ToDto(target.Id == existingDefaultId);
        }

        var persistedResult = await persistUpdatedDeploymentTarget(target, updated, cancellationToken);
        if (persistedResult is PersistUpdatedDeploymentTargetError persistenceError)
            return persistenceError switch
            {
                DeploymentTargetRevisionConflict conflict => conflict,
                DeploymentTargetPublicBaseDomainReferenced referenced => referenced,
                DeploymentTargetPublicBaseDomainConflict conflict => conflict,
            };

        var persisted = (DeploymentTarget)persistedResult.Value;
        if (!persisted.Enabled)
            invalidateDeploymentTargetKubernetesClient(target.Id);
        var defaultId = await loadDefaultDeploymentTargetId(cancellationToken);
        return persisted.ToDto(persisted.Id == defaultId);
    }
}

public sealed class UpdateDeploymentTargetRequestValidator : AbstractValidator<UpdateDeploymentTargetRequest>
{
    public UpdateDeploymentTargetRequestValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Deployment target ID");
        RuleFor(request => request.Body).NotNull().SetValidator(new UpdateDeploymentTargetRequestBodyValidator());
    }
}

public sealed class UpdateDeploymentTargetRequestBodyValidator : AbstractValidator<UpdateDeploymentTargetRequestBody>
{
    public UpdateDeploymentTargetRequestBodyValidator()
    {
        RuleFor(body => body.DisplayName).NotEmpty().MaximumLength(80);
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
        app.MapPut(
                "/{Id}",
                async (
                    IMediator mediator, [AsParameters] UpdateDeploymentTargetRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        DeploymentTargetDto target => Results.Ok(target),
                        DeploymentTargetNotFound error => DeploymentTargetHttpMappings.Map(error),
                        DeploymentTargetRevisionConflict error => DeploymentTargetHttpMappings.Map(error),
                        DeploymentTargetDeletionRejected error => DeploymentTargetHttpMappings.Map(error),
                        DeploymentTargetPublicBaseDomainReferenced error => DeploymentTargetHttpMappings.Map(error),
                        DeploymentTargetPublicBaseDomainConflict error => DeploymentTargetHttpMappings.Map(error),
                    });
                })
            .WithName("UpdateDeploymentTarget")
            .Produces<DeploymentTargetDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
