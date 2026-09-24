using Pole.Features.Users.Application.Identity;

namespace Pole.Tests.Integration.Infrastructure;

internal sealed class TestIdentityProvider : IIdentityProvider
{
    public Task<CreateIdentityUserOutcome> CreateUserAsync(
        CreateIdentityUser user,
        CancellationToken cancellationToken) =>
        Task.FromResult<CreateIdentityUserOutcome>(
            new IdentityUserCreated(Guid.NewGuid().ToString("N")));

    public Task<bool> DeleteUserAsync(
        string userId,
        CancellationToken cancellationToken) =>
        Task.FromResult(true);
}
