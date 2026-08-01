using FluentValidation;
using Pole.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.ResourceGroups.Application.Endpoints;

public sealed record CreateIsolationScopeRequest(
    [FromRoute] string ResourceGroupId,
    [FromBody] CreateIsolationScopeRequestBody Body,
    [FromHeader(Name = TenantHeaders.TenantId)] string TenantId)
    : IMediatorRequest<IsolationScopeDto>, ITenantCreateOperation;

public sealed record CreateIsolationScopeRequestBody(string Name);

public sealed class CreateIsolationScopeHandler(
    IResourceGroupDbContext dbContext,
    TimeProvider timeProvider)
    : IMediatorHandler<CreateIsolationScopeRequest, IsolationScopeDto>
{
    public async Task<Result<IsolationScopeDto>> HandleAsync(
        CreateIsolationScopeRequest request,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.RunInTransactionAsync<IsolationScopeDto>(async (session, ct) =>
        {
            var resourceGroupFilter = Builders<ResourceGroup>.Filter.And(
                Builders<ResourceGroup>.Filter.ForTenant(request.TenantId),
                Builders<ResourceGroup>.Filter.Eq(group => group.Id, request.ResourceGroupId));
            var resourceGroup = await dbContext.ResourceGroups
                .Find(session, resourceGroupFilter)
                .FirstOrDefaultAsync(ct);
            if (resourceGroup is null)
                return Failures.NotFound("Resource group not found.", request.ResourceGroupId);
            if (resourceGroup.Lifecycle is not ActiveResourceGroupLifecycleState)
                return Failures.Conflict("The resource group is being deleted.");

            var now = timeProvider.GetUtcNow().UtcDateTime;
            var fencedFilter = Builders<ResourceGroup>.Filter.And(
                resourceGroupFilter,
                Builders<ResourceGroup>.Filter.Eq(group => group.Revision, resourceGroup.Revision),
                Builders<ResourceGroup>.Filter
                    .OfType<ResourceGroupLifecycleState, ActiveResourceGroupLifecycleState>(
                        group => group.Lifecycle));
            var fence = await dbContext.ResourceGroups.UpdateOneAsync(
                session,
                fencedFilter,
                Builders<ResourceGroup>.Update
                    .Inc(group => group.Revision, 1)
                    .Set(group => group.UpdatedAtUtc, now),
                cancellationToken: ct);
            if (fence.ModifiedCount != 1)
                return Failures.Conflict("The resource group changed while the isolation scope was being created.");

            var scope = IsolationScopeFactory.CreateAdditional(
                request.TenantId,
                resourceGroup.Id,
                request.Body.Name,
                IsolationScopeFactory.DefaultOptions with
                {
                    CreatedAtUtc = now
                });
            await dbContext.IsolationScopes.InsertOneAsync(
                session,
                scope,
                cancellationToken: ct);
            await dbContext.StoreEventAsync(scope.ToCreatedEvent(), session, ct);

            return scope.ToDto();
        }, cancellationToken: cancellationToken);
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
                [AsParameters] CreateIsolationScopeRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(scope => Results.Created(
                    $"/api/v1/resource-groups/{scope.ResourceGroupId}/isolation-scopes/{scope.Id}",
                    scope));
            })
            .WithName("CreateIsolationScope")
            .WithSummary("Creates an additional isolation scope.")
            .Produces<IsolationScopeDto>(StatusCodes.Status201Created);

        return app;
    }
}
