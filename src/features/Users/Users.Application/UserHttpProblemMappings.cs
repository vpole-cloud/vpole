using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.Users.Application.Identity;
using Pole.Features.Users.Application.Persistence;

namespace Pole.Features.Users.Application;

public static class UserHttpProblemMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<UserEmailAlreadyExists>(MapUserEmailAlreadyExists),
            HttpProblemMapping.For<UserIdentityAlreadyExists>(MapUserIdentityAlreadyExists),
            HttpProblemMapping.For<IdentityUserCreationFailed>(MapIdentityUserCreationFailed)
        ]);

    private static HttpProblem MapUserEmailAlreadyExists(
        UserEmailAlreadyExists conflict) =>
        new(
            (int)HttpStatusCode.Conflict,
            PoleProblemTypeId.FromCode("users.email.already-exists"),
            "User email already exists",
            "users.email.already-exists",
            null,
            new Dictionary<string, object>
            {
                ["email"] = conflict.Email
            });

    private static HttpProblem MapIdentityUserCreationFailed(
        IdentityUserCreationFailed _) =>
        new(
            (int)HttpStatusCode.BadGateway,
            PoleProblemTypeId.FromCode("users.identity-provider.user-creation-failed"),
            "Identity provider rejected user creation",
            "users.identity-provider.user-creation-failed",
            "The identity provider could not create the user.");

    private static HttpProblem MapUserIdentityAlreadyExists(
        UserIdentityAlreadyExists _) =>
        new(
            (int)HttpStatusCode.Conflict,
            PoleProblemTypeId.FromCode("users.identity.already-exists"),
            "User identity already exists",
            "users.identity.already-exists");
}
