using System.Collections.Immutable;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Users.Contracts;
using Pole.Features.Users.Application.Common;
using Pole.Features.Users.Domain;
using Pole.Features.Users.Application.Identity;
using Pole.Mediator;
using Pole.ResultType;

namespace Pole.Features.Users.Application.Endpoints;

public static class CreateUser
{
    public static IEndpointRouteBuilder MapCreateUserEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapPost("/", async (IMediator mediator, CreateUserRequest request, CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(request, cancellationToken);
            return result.ToHttpResult(u => Results.Created($"/api/v1/users/{u.Id}", u));
        })
        .WithName("CreateUser")
        .Produces<UserDto>(StatusCodes.Status201Created);

        return app;
    }
}

public sealed record CreateUserRequest(
    [FromBody] CreateUserRequestBody Body)
    : IMediatorRequest<UserDto>;

public sealed record CreateUserRequestBody(
    string Email,
    string? FirstName,
    string? LastName);

public sealed class CreateUserHandler(
    IUserDbContext userDbContext,
    IIdentityProvider identityProvider,
    ILogger<CreateUserHandler> logger)
    : IMediatorHandler<CreateUserRequest, UserDto>
{
    public async Task<Result<UserDto>> HandleAsync(CreateUserRequest request, CancellationToken cancellationToken = default)
    {
        var email = NormalizeEmail(request.Body.Email);
        var existing = await userDbContext.Users.Find(u => u.Email == email).AnyAsync(cancellationToken);
        if (existing)
            return Failures.Conflict($"User '{email}' already exists.");

        var keycloakUserIdResult = await identityProvider.CreateUserAsync(
            new CreateIdentityUser(email, request.Body.FirstName, request.Body.LastName),
            cancellationToken);
        if (!keycloakUserIdResult.TryGetValue(out var keycloakUserId, out var keycloakFailure))
            return keycloakFailure;

        var defaultTenantId = ObjectId.GenerateNewId().ToString();
        var defaultTenantName = CreateDefaultTenantName(email);
        var user = new PoleUser(
            Id: keycloakUserId,
            Email: email,
            FirstName: request.Body.FirstName,
            LastName: request.Body.LastName,
            DefaultTenantId: defaultTenantId,
            CreatedAtUtc: DateTime.UtcNow);

        var result = await userDbContext.RunInTransactionAsync<UserDto>(async (session, ct) =>
        {
            await userDbContext.Users.InsertOneAsync(session, user, cancellationToken: ct);
            await userDbContext.StoreEventAsync(
                new UserCreatedEvent(
                    user.Id,
                    user.Email,
                    user.FirstName,
                    user.LastName,
                    user.DefaultTenantId,
                    defaultTenantName)
                {
                    EventCore = EventCore.Create()
                },
                session,
                ct);

            return user.ToDto();
        }, cancellationToken: cancellationToken);

        if (result.TryGetValue(out _, out _))
            return result;

        var cleanupSucceeded = await identityProvider.DeleteUserAsync(keycloakUserId, cancellationToken);
        if (!cleanupSucceeded)
            logger.LogError("Failed to delete Keycloak user {UserId} after Mongo user creation rollback.", keycloakUserId);

        return result;
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

