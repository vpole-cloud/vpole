using Pole.Features.Users.Application.Identity;
using Pole.ResultType;

namespace Pole.Tests.Integration.Infrastructure;

internal sealed class TestIdentityProvider : IIdentityProvider
{
    public Task<Result<string, IdentityUserCreationFailed>> CreateUserAsync(
        CreateIdentityUser user,
        CancellationToken cancellationToken) =>
        Task.FromResult<Result<string, IdentityUserCreationFailed>>(
            new Ok<string>(Guid.NewGuid().ToString("N")));

    public Task<bool> DeleteUserAsync(
        string userId,
        CancellationToken cancellationToken) =>
        Task.FromResult(true);
}
