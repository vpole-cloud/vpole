// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Users.Application.Common;
using Pole.Features.Users.Application.Identity;
using Pole.Features.Users.Application.Registration;
using Pole.Features.Users.Domain;
using Pole.Mediator;
using static Pole.Features.Users.Application.UserHttpProblemMappings;

namespace Pole.Features.Users.Application.Endpoints;

public static class CreateUser
{
    public static IEndpointRouteBuilder MapCreateUserEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost(
                "/",
                async (
                    IMediator mediator, [AsParameters] CreateUserRequest request,
                    CancellationToken cancellationToken) =>
                {
                    var result = await mediator.Send(request, cancellationToken);
                    return result.ToHttpResult(outcome => outcome switch
                    {
                        UserDto user => Results.Created($"/api/v1/users/{user.Id}", user),
                        UserEmailAlreadyExists error => MapUserEmailAlreadyExists(error),
                        UserIdentityAlreadyExists error => MapUserIdentityAlreadyExists(error),
                        IdentityUserCreationFailed error => MapIdentityUserCreationFailed(error),
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
    [FromBody] CreateUserRequestBody Body
) : IMediatorRequest<CreateUserOutcome>;

public sealed record CreateUserRequestBody(
    string Email,
    string? FirstName,
    string? LastName
);

public union CreateUserOutcome(
    UserDto,
    UserEmailAlreadyExists,
    UserIdentityAlreadyExists,
    IdentityUserCreationFailed);

public sealed class CreateUserHandler(
    EnsureUserEmailAvailable ensureUserEmailAvailable,
    PersistCreatedUser persistCreatedUser,
    IIdentityProvider identityProvider,
    ILogger<CreateUserHandler> logger,
    TimeProvider timeProvider
) : IMediatorOutcomeHandler<CreateUserRequest, CreateUserOutcome>
{
    public async Task<CreateUserOutcome> HandleAsync(
        CreateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(request.Body.Email);
        var available = await ensureUserEmailAvailable(email, cancellationToken);
        if (available is UserEmailAlreadyExists existing)
            return existing;

        var identityUser = await identityProvider.CreateUserAsync(
            new CreateIdentityUser(email, request.Body.FirstName, request.Body.LastName),
            cancellationToken);
        return identityUser switch
        {
            IdentityUserCreated created => await PersistAsync(request, email, created.UserId, cancellationToken),
            IdentityUserCreationFailed error => error,
        };
    }

    private async Task<CreateUserOutcome> PersistAsync(
        CreateUserRequest request,
        string email,
        string keycloakUserId,
        CancellationToken cancellationToken)
    {
        var defaultTenantId = ObjectId.GenerateNewId().ToString();
        var defaultTenantName = CreateDefaultTenantName(email);
        var user = PoleUserFactory.Create(
            id: keycloakUserId,
            email: email,
            firstName: request.Body.FirstName,
            lastName: request.Body.LastName,
            defaultTenantId: defaultTenantId,
            createdAtUtc: timeProvider.GetUtcNow().UtcDateTime);

        var persisted = await persistCreatedUser(
            new PersistCreatedUserRequest(user, defaultTenantName),
            cancellationToken);
        return persisted switch
        {
            UserPersisted => user.ToDto(),
            CreateUserPersistenceError error => await CompensateAsync(keycloakUserId, error, cancellationToken),
        };
    }

    private async Task<CreateUserOutcome> CompensateAsync(
        string keycloakUserId,
        CreateUserPersistenceError error,
        CancellationToken cancellationToken)
    {
        var cleanupSucceeded = await identityProvider.DeleteUserAsync(keycloakUserId, cancellationToken);
        if (!cleanupSucceeded)
            logger.LogError(
                "Failed to delete Keycloak user {UserId} after Mongo user creation rollback.",
                keycloakUserId);

        return error switch
        {
            UserEmailAlreadyExists conflict => conflict,
            UserIdentityAlreadyExists conflict => conflict,
        };
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
