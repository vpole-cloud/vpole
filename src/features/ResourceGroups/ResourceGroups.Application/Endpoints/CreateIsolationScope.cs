using FluentValidation;
using Pole.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Application.Persistence;
using Pole.Features.ResourceGroups.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public sealed record CreateIsolationScopeRequest(
    [FromRoute] string ResourceGroupId,
    [FromBody] CreateIsolationScopeRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<CreateIsolationScopeOutcome>, ITenantCreateOperation;

public union CreateIsolationScopeError(
    ResourceGroupNotFound,
    ResourceGroupDeleting,
    ResourceGroupRevisionConflict,
    IsolationScopeNameConflict);

public union CreateIsolationScopeOutcome(IsolationScopeDto, CreateIsolationScopeError);

public sealed record CreateIsolationScopeRequestBody(string Name);

public sealed class CreateIsolationScopeHandler(
    LoadResourceGroup loadResourceGroup,
    PersistCreatedIsolationScope persistCreatedIsolationScope,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<CreateIsolationScopeRequest, CreateIsolationScopeOutcome>
{
    public async Task<CreateIsolationScopeOutcome> HandleAsync(
        CreateIsolationScopeRequest request,
        CancellationToken cancellationToken = default)
    {
        var resourceGroup = await loadResourceGroup(
            request.TenantId,
            request.ResourceGroupId,
            cancellationToken);
        if (resourceGroup is null)
            return new CreateIsolationScopeError(
                new ResourceGroupNotFound(request.ResourceGroupId));
        if (resourceGroup.Lifecycle is DeletingResourceGroupLifecycleState)
            return new CreateIsolationScopeError(
                new ResourceGroupDeleting(resourceGroup.Id));
        if (resourceGroup.Lifecycle is not ActiveResourceGroupLifecycleState)
        {
            throw new InvalidOperationException(
                $"Resource group '{resourceGroup.Id}' has an unsupported lifecycle state.");
        }

        var scope = IsolationScopeFactory.CreateAdditional(
            request.TenantId,
            resourceGroup.Id,
            request.Body.Name,
            IsolationScopeFactory.DefaultOptions with
            {
                CreatedAtUtc = timeProvider.GetUtcNow().UtcDateTime
            });
        var persisted = await persistCreatedIsolationScope(
            resourceGroup,
            scope,
            cancellationToken);
        return persisted switch
        {
            Ok<IsolationScope> success => success.Value.ToDto(),
            Err<PersistCreatedIsolationScopeError> error => error.Value switch
            {
                ResourceGroupRevisionConflict conflict => new CreateIsolationScopeError(conflict),
                IsolationScopeNameConflict conflict => new CreateIsolationScopeError(conflict)
            }
        };
    }
}

public sealed class CreateIsolationScopeRequestValidator
    : AbstractValidator<CreateIsolationScopeRequest>
{
    public CreateIsolationScopeRequestValidator()
    {
        RuleFor(request => request.ResourceGroupId)
            .MongoObjectId("Resource group ID");
        RuleFor(request => request.Body)
            .NotNull()
            .SetValidator(new CreateIsolationScopeRequestBodyValidator());
    }
}

public sealed class CreateIsolationScopeRequestBodyValidator
    : AbstractValidator<CreateIsolationScopeRequestBody>
{
    public CreateIsolationScopeRequestBodyValidator() =>
        RuleFor(body => body.Name)
            .NotEmpty()
            .MaximumLength(IsolationScopeFactory.MaximumNameLength)
            .Must(name =>
                name is not null &&
                !string.Equals(
                    name.Trim(),
                    IsolationScopeFactory.DefaultScopeName,
                    StringComparison.OrdinalIgnoreCase))
            .WithMessage("The isolation scope name 'default' is reserved.");
}

public static class CreateIsolationScopeEndpoint
{
    public static IEndpointRouteBuilder MapCreateIsolationScopeEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/{ResourceGroupId}/isolation-scopes", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] CreateIsolationScopeRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    IsolationScopeDto scope => Results.Created(
                        $"/api/v1/resource-groups/{scope.ResourceGroupId}/isolation-scopes/{scope.Id}",
                        scope),
                    CreateIsolationScopeError error => httpProblems.Map(error).ToHttpResult()
                });
            })
            .WithName("CreateIsolationScope")
            .WithSummary("Creates an additional isolation scope.")
            .Produces<IsolationScopeDto>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return app;
    }
}
