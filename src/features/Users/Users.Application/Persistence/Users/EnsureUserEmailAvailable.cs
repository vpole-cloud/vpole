using MongoDB.Driver;
using Pole.Features.Users.Domain;

namespace Pole.Features.Users.Application.Persistence.Users;

public sealed record UserEmailAvailable;

public union EnsureUserEmailAvailableOutcome(
    UserEmailAvailable,
    UserEmailAlreadyExists);

public delegate Task<EnsureUserEmailAvailableOutcome> EnsureUserEmailAvailable(
    string email,
    CancellationToken cancellationToken = default);

internal sealed class MongoEnsureUserEmailAvailable(IMongoDatabase database)
{
    private readonly IMongoCollection<PoleUser> _users = database.GetCollection<PoleUser>("Users");

    public async Task<EnsureUserEmailAvailableOutcome> InvokeAsync(
        string email,
        CancellationToken cancellationToken = default) =>
        await _users
            .Find(UserFilters.ByEmail(email))
            .AnyAsync(cancellationToken)
            ? new UserEmailAlreadyExists(email)
            : new UserEmailAvailable();
}
