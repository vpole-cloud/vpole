using Pole.ResultType;

namespace Pole.Features.Users.Identity;

public sealed record CreateIdentityUser(
    string Email,
    string? FirstName,
    string? LastName);

public interface IIdentityProvider
{
    Task<Result<string>> CreateUserAsync(CreateIdentityUser user, CancellationToken cancellationToken);
    Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken);
}
