using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Users.Domain;
using Pole.Features.Users.Events;
using Pole.ResultType;

namespace Pole.Features.Users.Application.Persistence.Users;

public sealed record PersistCreatedUserRequest(
    PoleUser User,
    string DefaultTenantName);

public delegate Task<Result<Unit, CreateUserPersistenceError>> PersistCreatedUser(
    PersistCreatedUserRequest request,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedUser(
    IMongoDatabase database,
    MongoRunTransaction transaction,
    StoreOutboxWrites storeOutboxWrites)
{
    private readonly IMongoCollection<PoleUser> _users = database.GetCollection<PoleUser>("Users");

    public async Task<Result<Unit, CreateUserPersistenceError>> InvokeAsync(
        PersistCreatedUserRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await transaction.InvokeOutcomeAsync<Result<Unit, CreateUserPersistenceError>>(
                async (context, transactionCancellationToken) =>
                {
                    var emailExists = await _users
                        .Find(UserFilters.ByEmail(request.User.Email))
                        .AnyAsync(transactionCancellationToken);
                    if (emailExists)
                    {
                        return context.AbortError<Unit, CreateUserPersistenceError>(
                            new CreateUserPersistenceError(
                                new UserEmailAlreadyExists(request.User.Email)));
                    }

                    await _users.InsertOneAsync(
                        context.Session,
                        request.User,
                        cancellationToken: transactionCancellationToken);
                    await storeOutboxWrites(
                        context.Session,
                        [OutboxWrite.Create(new UserCreatedEvent(
                            request.User.Id,
                            request.User.Email,
                            request.User.FirstName,
                            request.User.LastName,
                            request.User.DefaultTenantId,
                            request.DefaultTenantName)
                        {
                            EventCore = EventCore.Create()
                        })],
                        transactionCancellationToken);
                    return context.Commit(new Ok<Unit>(Unit.Value));
                },
                cancellationToken);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new Err<CreateUserPersistenceError>(
                new CreateUserPersistenceError(
                    new UserIdentityAlreadyExists(request.User.Id)));
        }
    }
}
