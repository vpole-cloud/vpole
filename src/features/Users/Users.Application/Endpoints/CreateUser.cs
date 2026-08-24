using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Users.Application.Common;
using Pole.Features.Users.Domain;
using Pole.Features.Users.Application.Identity;
using Pole.Features.Users.Application.Persistence;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Users.Application.Endpoints;

public static class CreateUser
{
    public static IEndpointRouteBuilder MapCreateUserEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (
                IMediator mediator,
                IHttpProblemMapper httpProblems,
                [AsParameters] CreateUserRequest request,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(request, cancellationToken);
                return result.ToHttpResult(outcome => outcome switch
                {
                    UserDto user => Results.Created($"/api/v1/users/{user.Id}", user),
                    CreateUserError error => httpProblems.Map(error).ToHttpResult()
                });
            })
        .WithName("CreateUser")
        .RequireAuthorization(Policy.Admin)
        .Produces<UserDto>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status502BadGateway);

        return app;
    }
}

public sealed record CreateUserRequest(
    [FromBody] CreateUserRequestBody Body)
    : IMediatorRequest<CreateUserOutcome>;

public sealed record CreateUserRequestBody(
    string Email,
    string? FirstName,
    string? LastName);

public union CreateUserError(
    CreateUserPersistenceError,
    IdentityUserCreationFailed);

public union CreateUserOutcome(UserDto, CreateUserError);

public sealed class CreateUserHandler(
    EnsureUserEmailAvailable ensureUserEmailAvailable,
    PersistCreatedUser persistCreatedUser,
    IIdentityProvider identityProvider,
    ILogger<CreateUserHandler> logger,
    TimeProvider timeProvider)
    : IMediatorOutcomeHandler<CreateUserRequest, CreateUserOutcome>
{
    public async Task<CreateUserOutcome> HandleAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(request.Body.Email);
        var available = await ensureUserEmailAvailable(email, cancellationToken);
        if (available is Err<CreateUserPersistenceError> existing)
            return new CreateUserOutcome(new CreateUserError(existing.Value));

        var identityUser = await identityProvider.CreateUserAsync(
            new CreateIdentityUser(email, request.Body.FirstName, request.Body.LastName),
            cancellationToken);
        var identityResult = TypedResultExtensions.TryGetValue(identityUser);
        if (!identityResult.Succeeded)
            return new CreateUserOutcome(new CreateUserError(identityResult.Error!));
        var keycloakUserId = identityResult.Value!;

        var defaultTenantId = ObjectId.GenerateNewId().ToString();
        var defaultTenantName = CreateDefaultTenantName(email);
        var user = new PoleUser(
            Id: keycloakUserId,
            Email: email,
            FirstName: request.Body.FirstName,
            LastName: request.Body.LastName,
            DefaultTenantId: defaultTenantId,
            CreatedAtUtc: timeProvider.GetUtcNow().UtcDateTime);

        var persisted = await persistCreatedUser(
            new PersistCreatedUserRequest(user, defaultTenantName),
            cancellationToken);
        var persistenceResult = TypedResultExtensions.TryGetValue(persisted);
        if (persistenceResult.Succeeded)
            return user.ToDto();

        var cleanupSucceeded = await identityProvider.DeleteUserAsync(keycloakUserId, cancellationToken);
        if (!cleanupSucceeded)
            logger.LogError("Failed to delete Keycloak user {UserId} after Mongo user creation rollback.", keycloakUserId);

        return new CreateUserOutcome(new CreateUserError(persistenceResult.Error!));
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string CreateDefaultTenantName(string email)
    {
        var name = $"{email} personal";
        return name.Length <= 80 ? name : name[..80];
    }
}

public sealed class CreateUserValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Body.Email).NotEmpty().EmailAddress().MaximumLength(254);
        RuleFor(x => x.Body.FirstName).MaximumLength(80);
        RuleFor(x => x.Body.LastName).MaximumLength(80);
    }
}
