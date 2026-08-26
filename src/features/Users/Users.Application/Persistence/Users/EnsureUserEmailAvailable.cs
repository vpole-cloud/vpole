using MongoDB.Driver;
using Pole.Features.Users.Domain;
using Pole.ResultType;

namespace Pole.Features.Users.Application.Persistence.Users;

public delegate Task<Result<Unit, CreateUserPersistenceError>> EnsureUserEmailAvailable(
    string email,
    CancellationToken cancellationToken = default);

internal sealed class MongoEnsureUserEmailAvailable(IMongoDatabase database)
{
    private readonly IMongoCollection<PoleUser> _users = database.GetCollection<PoleUser>("Users");

    public async Task<Result<Unit, CreateUserPersistenceError>> InvokeAsync(
        string email,
        CancellationToken cancellationToken = default) =>
        await _users
            .Find(UserFilters.ByEmail(email))
            .AnyAsync(cancellationToken)
            ? new Err<CreateUserPersistenceError>(
                new CreateUserPersistenceError(new UserEmailAlreadyExists(email)))
            : new Ok<Unit>(Unit.Value);
}
