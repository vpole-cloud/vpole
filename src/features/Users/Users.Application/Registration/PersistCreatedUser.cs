using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Users.Application.Mongo;
using Pole.Features.Users.Domain;
using Pole.Features.Users.Events;

namespace Pole.Features.Users.Application.Registration;

public sealed record PersistCreatedUserRequest(
    PoleUser User,
    string DefaultTenantName);

public sealed record UserPersisted;

public union PersistCreatedUserOutcome(
    UserPersisted,
    CreateUserPersistenceError);

public delegate Task<PersistCreatedUserOutcome> PersistCreatedUser(
    PersistCreatedUserRequest request,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistCreatedUser(
    IMongoCollection<PoleUser> _users,
    MongoRunTransaction transaction,
    StoreOutboxWrites storeOutboxWrites)
{
    public async Task<PersistCreatedUserOutcome> InvokeAsync(
        PersistCreatedUserRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await transaction.InvokeOutcomeAsync<PersistCreatedUserOutcome>(
                async (context, transactionCancellationToken) =>
                {
                    var emailExists = await _users
                        .Find(UserFilters.ByEmail(request.User.Email))
                        .AnyAsync(transactionCancellationToken);
                    if (emailExists)
                    {
                        return context.Abort(
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
                    return context.Commit(new UserPersisted());
                },
                cancellationToken);
        }
        catch (MongoWriteException exception) when (
            exception.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            return new CreateUserPersistenceError(
                new UserIdentityAlreadyExists(request.User.Id));
        }
    }
}
