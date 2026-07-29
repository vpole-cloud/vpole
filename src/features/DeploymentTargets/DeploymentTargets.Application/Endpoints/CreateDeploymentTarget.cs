using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
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
    : IMediatorRequest<DeploymentTargetDto>;

public sealed class CreateDeploymentTargetHandler(
    DeploymentTargetPersistence persistence,
    IDeploymentTargetConnectionPreparer connectionPreparer,
    IDeploymentTargetProbe probe,
    TimeProvider timeProvider)
    : IMediatorHandler<CreateDeploymentTargetRequest, DeploymentTargetDto>
{
    public async Task<Result<DeploymentTargetDto>> HandleAsync(
        CreateDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var id = ObjectId.GenerateNewId().ToString();
        var preparedResult = await connectionPreparer.PrepareAsync(
            id,
            connectionVersion: 1,
            request.Body.Connection.ToInput(),
            cancellationToken);
        if (!preparedResult.TryGetValue(out var prepared, out var prepareFailure))
            return prepareFailure;

        using (prepared)
        {
            var observation = await probe.ProbeAsync(
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
            var inserted = await persistence.InsertAsync(target, cancellationToken);
            return inserted.TryGetValue(out var persisted, out var insertionFailure)
                ? persisted.ToDto()
                : insertionFailure;
        }
    }

    private static ValidationFailure ConnectionValidationFailure(string safeMessage) =>
        Failures.ValidationFailure(new Dictionary<string, string[]>
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
                [AsParameters] CreateDeploymentTargetRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(target =>
                    Results.Created($"/api/v1/platform/deployment-targets/{target.Id}", target));
            })
            .WithName("CreateDeploymentTarget")
            .Produces<DeploymentTargetDto>(StatusCodes.Status201Created);
        return app;
    }
}
