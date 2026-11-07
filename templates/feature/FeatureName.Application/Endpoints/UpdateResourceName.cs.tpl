using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.FeatureName.Application.Common;
using Pole.Features.FeatureName.Application.Updates;
using Pole.Features.FeatureName.Domain;
using Pole.Mediator;
using Pole.Validation;
using static Pole.Features.FeatureName.Application.ResourceNameHttpProblems;

namespace Pole.Features.FeatureName.Application.Endpoints;

public static class UpdateResourceNameEndpoint
{
    public static IEndpointRouteBuilder MapUpdateResourceNameEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/{Id}", async (IMediator mediator, [AsParameters] UpdateResourceNameRequest request,
                CancellationToken ct) => (await mediator.Send(request, ct)).ToHttpResult(outcome => outcome switch
                {
                    ResourceNameDto value => Results.Ok(value),
                    ResourceNameNotFound error => MapMissing(error),
                    ResourceNameRevisionConflict error => MapRevisionConflict(error),
                    ResourceNameNameAlreadyExists error => MapNameConflict(error)
                }))
            .WithName("FeatureName.UpdateResourceName")
            .Produces<ResourceNameDto>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return endpoints;
    }
}

public sealed record UpdateResourceNameRequest(
    [FromRoute] string Id,
    [FromBody] UpdateResourceNameBody Body,
    [FromTenantHeader] string TenantId) : IMediatorRequest<UpdateResourceNameOutcome>, ITenantWriteOperation;

public sealed record UpdateResourceNameBody(
    long ExpectedRevision,
    ResourceNameDetailsDto Details);

public union UpdateResourceNameOutcome(
    ResourceNameDto,
    ResourceNameNotFound,
    ResourceNameRevisionConflict,
    ResourceNameNameAlreadyExists);

public sealed class UpdateResourceNameValidator : AbstractValidator<UpdateResourceNameRequest>
{
    public UpdateResourceNameValidator()
    {
        RuleFor(request => request.Id).Length(24).MongoObjectId("Resource ID");
        RuleFor(request => request.Body).NotNull();
        When(request => request.Body is not null, () =>
        {
            RuleFor(request => request.Body.ExpectedRevision).GreaterThan(0);
            RuleFor(request => request.Body.Details).NotNull().SetValidator(new ResourceNameDetailsValidator());
        });
    }
}

public sealed class UpdateResourceNameHandler(PersistUpdatedResourceName persist)
    : IMediatorOutcomeHandler<UpdateResourceNameRequest, UpdateResourceNameOutcome>
{
    public async Task<UpdateResourceNameOutcome> HandleAsync(UpdateResourceNameRequest request,
        CancellationToken cancellationToken = default)
    {
        var details = ResourceNameDetails.Create(request.Body.Details.Name, request.Body.Details.Description);
        var result = await persist(new(request.TenantId, request.Id, request.Body.ExpectedRevision, details),
            cancellationToken);
        return result switch
        {
            ResourceName value => ResourceNameDto.From(value),
            ResourceNameNotFound error => error,
            ResourceNameRevisionConflict error => error,
            ResourceNameNameAlreadyExists error => error
        };
    }
}
