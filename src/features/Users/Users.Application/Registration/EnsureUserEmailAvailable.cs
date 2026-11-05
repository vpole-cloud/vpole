using MongoDB.Driver;
using Pole.Features.Users.Application.Mongo;
using Pole.Features.Users.Domain;

namespace Pole.Features.Users.Application.Registration;

public sealed record UserEmailAvailable;

public union EnsureUserEmailAvailableOutcome(
    UserEmailAvailable,
    UserEmailAlreadyExists);

public delegate Task<EnsureUserEmailAvailableOutcome> EnsureUserEmailAvailable(
    string email,
    CancellationToken cancellationToken = default);

internal sealed class MongoEnsureUserEmailAvailable(IMongoCollection<PoleUser> _users)
{
    public async Task<EnsureUserEmailAvailableOutcome> InvokeAsync(
        string email,
        CancellationToken cancellationToken = default) =>
        await _users
            .Find(UserFilters.ByEmail(email))
            .AnyAsync(cancellationToken)
            ? new UserEmailAlreadyExists(email)
            : new UserEmailAvailable();
}
