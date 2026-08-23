using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Persistence;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record CreateDeploymentTargetRequestBody(
    string DisplayName,
    bool Enabled,
    bool AcceptsWorkloads,
    DeploymentTargetWorkloadConfigurationRequestBody WorkloadConfiguration,
    DeploymentTargetConnectionRequestBody Connection);

public sealed record CreateDeploymentTargetRequest(
    [FromBody] CreateDeploymentTargetRequestBody Body)
    : IMediatorRequest<CreateDeploymentTargetOutcome>;

public union CreateDeploymentTargetOutcome(
    DeploymentTargetDto,
    DeploymentTargetConnectionRejected,
    DeploymentTargetPublicBaseDomainConflict,
    DeploymentTargetIdentityConflict);

public sealed class CreateDeploymentTargetHandler(
    PersistCreatedDeploymentTarget persistCreatedDeploymentTarget,
    PrepareDeploymentTargetConnection prepareDeploymentTargetConnection,
    ProbeDeploymentTargetConnection probeDeploymentTargetConnection,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<CreateDeploymentTargetRequest, CreateDeploymentTargetOutcome>
{
    public async Task<CreateDeploymentTargetOutcome> HandleAsync(
        CreateDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var id = ObjectId.GenerateNewId().ToString();
        var preparedResult = await prepareDeploymentTargetConnection(
            id,
            connectionVersion: 1,
            request.Body.Connection.ToInput(),
            cancellationToken);
        if (!preparedResult.TryGetValue(out var prepared, out var prepareError))
            return prepareError;

        using (prepared)
        {
            var observation = await probeDeploymentTargetConnection(
                prepared.Candidate,
                prepared.Connection.Version,
                cancellationToken: cancellationToken);
            if (!observation.IsUsable || observation.ClusterIdentity is null)
            {
                return ConnectionValidationFailure(
                    observation.Health.FailureMessage ?? "The Kubernetes connection is not usable.");
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var target = DeploymentTargetFactory.Create(
                request.Body.DisplayName,
                observation.ClusterIdentity,
                prepared.Connection,
                observation.Health,
                observation.Capabilities,
                request.Body.WorkloadConfiguration.ToDomain(),
                request.Body.Enabled,
                request.Body.AcceptsWorkloads,
                new DeploymentTargetFactory.CreateOptions(id, now));
            var inserted = await persistCreatedDeploymentTarget(target, cancellationToken);
            if (inserted.TryGetValue(out var persisted, out var persistenceError))
                return persisted.ToDto();

            return persistenceError switch
            {
                DeploymentTargetPublicBaseDomainConflict conflict => conflict,
                DeploymentTargetIdentityConflict conflict => conflict
            };
        }
    }

    private static DeploymentTargetConnectionRejected ConnectionValidationFailure(string safeMessage) =>
        new(new Dictionary<string, string[]>
        {
            ["Body.Connection"] = [safeMessage]
        });

}

public sealed class CreateDeploymentTargetRequestValidator
    : AbstractValidator<CreateDeploymentTargetRequest>
{
    public CreateDeploymentTargetRequestValidator() =>
        RuleFor(request => request.Body)
            .NotNull()
            .SetValidator(new CreateDeploymentTargetRequestBodyValidator());
}

public sealed class CreateDeploymentTargetRequestBodyValidator
    : AbstractValidator<CreateDeploymentTargetRequestBody>
{
    public CreateDeploymentTargetRequestBodyValidator()
    {
        RuleFor(body => body.DisplayName)
            .NotEmpty()
            .MaximumLength(80);
        RuleFor(body => body.AcceptsWorkloads)
            .Equal(false)
            .When(body => !body.Enabled)
            .WithMessage("A disabled deployment target cannot accept workloads.");
        RuleFor(body => body.WorkloadConfiguration)
            .NotNull()
            .SetValidator(new DeploymentTargetWorkloadConfigurationRequestBodyValidator());
        RuleFor(body => body.Connection)
            .NotNull()
            .SetValidator(new DeploymentTargetConnectionRequestBodyValidator());
    }
}

public static class CreateDeploymentTargetEndpoint
{
    public static IEndpointRouteBuilder MapCreateDeploymentTargetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] CreateDeploymentTargetRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    DeploymentTargetDto target => Results.Created(
                        $"/api/v1/platform/deployment-targets/{target.Id}",
                        target),
                    DeploymentTargetConnectionRejected error => httpProblems.Map(error).ToHttpResult(),
                    DeploymentTargetPublicBaseDomainConflict error => httpProblems.Map(error).ToHttpResult(),
                    DeploymentTargetIdentityConflict error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("CreateDeploymentTarget")
            .Produces<DeploymentTargetDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);
        return app;
    }
}
