using Pole.ResultType;

namespace Pole.Features.Users.Application.Identity;

public sealed record CreateIdentityUser(
    string Email,
    string? FirstName,
    string? LastName);

public sealed record IdentityUserCreationFailed;

public interface IIdentityProvider
{
    Task<Result<string, IdentityUserCreationFailed>> CreateUserAsync(
        CreateIdentityUser user,
        CancellationToken cancellationToken);
    Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken);
}
