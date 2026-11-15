using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Users.Application.Mongo;
using Pole.Features.Users.Contracts;
using Pole.Features.Users.Domain;

namespace Pole.Features.Users.Application.Registration;

internal sealed class MongoJoinInvitedUser(
    IMongoCollection<PoleUser> users,
    TimeProvider timeProvider
)
{
    public async Task<JoinInvitedUserOutcome> InvokeAsync(
        JoinInvitedUserRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Joining an invited user requires a membership transaction.");

        var existing = await users.Find(session, UserFilters.ById(request.Subject))
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is not null && existing.Email != request.VerifiedEmail)
            return new InvitedUserIdentityConflict();

        var emailOwner = await users.Find(session, UserFilters.ByEmail(request.VerifiedEmail))
            .SingleOrDefaultAsync(cancellationToken);
        if (emailOwner is not null && emailOwner.Id != request.Subject)
            return new InvitedUserIdentityConflict();

        var defaultTenantId = string.IsNullOrEmpty(existing?.DefaultTenantId)
            ? request.TenantId
            : existing.DefaultTenantId;
        var update = Builders<PoleUser>.Update
            .SetOnInsert(item => item.Email, request.VerifiedEmail)
            .SetOnInsert(item => item.FirstName, request.FirstName)
            .SetOnInsert(item => item.LastName, request.LastName)
            .Set(item => item.DefaultTenantId, defaultTenantId)
            .SetOnInsert(item => item.CreatedAtUtc, timeProvider.GetUtcNow().UtcDateTime)
            .Inc(item => item.MembershipFence, 1);
        try
        {
            var user = await users.FindOneAndUpdateAsync(
                session,
                UserFilters.ById(request.Subject),
                update,
                new FindOneAndUpdateOptions<PoleUser> { IsUpsert = true, ReturnDocument = ReturnDocument.After },
                cancellationToken);
            return new InvitedUserJoined(user.DefaultTenantId);
        }
        catch (Exception error) when (MongoDuplicateKey.IsForIndex(
                                          error,
                                          UserIndexes.Email,
                                          users.CollectionNamespace.CollectionName))
        {
            return new InvitedUserIdentityConflict();
        }
    }
}
