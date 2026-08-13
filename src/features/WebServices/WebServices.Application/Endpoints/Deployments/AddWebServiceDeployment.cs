using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.WebServices.Application.Operations.Deployments;
using Pole.Mediator;
using Pole.ResultType;
using Pole.Validation;

namespace Pole.Features.WebServices.Application.Endpoints;

public static class AddWebServiceDeployment
{
    extension(IEndpointRouteBuilder app)
    {
        public IEndpointRouteBuilder MapAddWebServiceDeploymentEndpoint()
        {
            app.MapPost(
                    "/{Id}/deployments",
                    async (
                        IMediator mediator,
                        [AsParameters] AddWebServiceDeploymentRequest request,
                        CancellationToken cancellationToken) =>
                        (await mediator.Send(request, cancellationToken))
                        .ToHttpResult(value => Results.Accepted(value: value)))
                .WithName("AddWebServiceDeployment")
                .Produces<PlacementSummaryDto>(StatusCodes.Status202Accepted);
            return app;
        }
    }
}

public sealed record AddWebServiceDeploymentRequest(
    [FromRoute] string Id,
    [FromBody] WebServiceDeploymentTargetRequest Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId,
    [FromClaim(Claims.UserId)] ClaimValue<string?> ActorUserId = default,
    [FromClaim(Claims.Email)] ClaimValue<string?> ActorEmail = default)
    : IMediatorRequest<PlacementSummaryDto>, ITenantWriteOperation;

public sealed class AddWebServiceDeploymentHandler(
    AddWebServiceDeploymentOperation operation)
    : IMediatorHandler<AddWebServiceDeploymentRequest, PlacementSummaryDto>
{
    public Task<Result<PlacementSummaryDto>> HandleAsync(
        AddWebServiceDeploymentRequest request,
        CancellationToken cancellationToken = default) =>
        operation.ExecuteAsync(
            new AddWebServiceDeploymentCommand(
                request.TenantId,
                request.Id,
                request.Body.DeploymentTargetId!.Trim(),
                request.Body.FixedReplicas,
                request.Body.ComputeProfileId,
                request.ActorUserId.Value,
                request.ActorEmail.Value),
            cancellationToken);
}

public sealed class AddWebServiceDeploymentValidator : AbstractValidator<AddWebServiceDeploymentRequest>
{
    public AddWebServiceDeploymentValidator()
    {
        RuleFor(request => request.Id).MongoObjectId("Web service ID");
        RuleFor(request => request.Body)
            .SetValidator(new WebServiceDeploymentTargetRequestValidator());
        RuleFor(request => request.Body.DeploymentTargetId)
            .NotEmpty()
            .MongoObjectId("Deployment target ID");
    }
}
