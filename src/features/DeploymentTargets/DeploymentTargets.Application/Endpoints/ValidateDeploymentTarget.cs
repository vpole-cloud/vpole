using FluentValidation;
using Pole.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Connections;
using Pole.Features.DeploymentTargets.Application.Validation;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.DeploymentTargets.Application.Endpoints;

public sealed record ValidateDeploymentTargetRequest(
    [FromRoute] string Id)
    : IMediatorRequest<DeploymentTargetDto>;

public sealed class ValidateDeploymentTargetHandler(
    IDeploymentTargetDbContext dbContext,
    DeploymentTargetReader reader,
    DeploymentTargetValidationService validationService)
    : IMediatorHandler<ValidateDeploymentTargetRequest, DeploymentTargetDto>
{
    public async Task<Result<DeploymentTargetDto>> HandleAsync(
        ValidateDeploymentTargetRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetResult = await reader.GetAsync(request.Id, cancellationToken);
        if (!targetResult.TryGetValue(out var target, out var targetFailure))
            return targetFailure;

        var observation = await validationService.ProbeStoredConnectionAsync(
            target,
            cancellationToken);

        var updated = target.ApplyValidationObservation(
            target.Revision,
            target.Connection.Version,
            observation.Health,
            observation.Capabilities);
        var persisted = await dbContext.DeploymentTargets.FindOneAndUpdateAsync(
            Builders<DeploymentTarget>.Filter.And(
                DeploymentTargetFilters.RevisionFilter(target),
                Builders<DeploymentTarget>.Filter.Eq(
                    item => item.Connection.Version,
                    target.Connection.Version)),
            Builders<DeploymentTarget>.Update.Combine(
                Builders<DeploymentTarget>.Update.Set(item => item.Health, updated.Health),
                Builders<DeploymentTarget>.Update.Set(item => item.Capabilities, updated.Capabilities)),
            new FindOneAndUpdateOptions<DeploymentTarget>
            {
                ReturnDocument = ReturnDocument.After
            },
            cancellationToken);
        if (persisted is null)
            return Failures.Conflict("The deployment target changed while it was being validated.");

        var defaultId = await reader.GetDefaultIdAsync(cancellationToken);
        return persisted.ToDto(persisted.Id == defaultId);
    }
}

public sealed class ValidateDeploymentTargetRequestValidator
    : AbstractValidator<ValidateDeploymentTargetRequest>
{
    public ValidateDeploymentTargetRequestValidator() =>
        RuleFor(request => request.Id)
            .MongoObjectId("Deployment target ID");
}

public static class ValidateDeploymentTargetEndpoint
{
    public static IEndpointRouteBuilder MapValidateDeploymentTargetEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{Id}/validate", async (
                IMediator mediator,
                [AsParameters] ValidateDeploymentTargetRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(Results.Ok);
            })
            .WithName("ValidateDeploymentTarget")
            .Produces<DeploymentTargetDto>();
        return app;
    }
}
