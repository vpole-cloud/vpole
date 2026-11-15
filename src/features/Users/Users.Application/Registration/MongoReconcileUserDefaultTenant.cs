using MongoDB.Driver;
using Pole.Features.Users.Application.Mongo;
using Pole.Features.Users.Contracts;
using Pole.Features.Users.Domain;

namespace Pole.Features.Users.Application.Registration;

internal sealed class MongoReconcileUserDefaultTenant(
    IMongoCollection<PoleUser> users
)
{
    public async Task InvokeAsync(
        ReconcileUserDefaultTenantRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        if (!session.IsInTransaction)
            throw new InvalidOperationException("Default tenant reconciliation requires a membership transaction.");
        var user = await users.Find(session, UserFilters.ById(request.Subject)).SingleOrDefaultAsync(cancellationToken);
        if (user is null)
            return;
        var defaultTenant = request.RemainingTenantIds.Contains(user.DefaultTenantId)
            ? user.DefaultTenantId
            : request.RemainingTenantIds.FirstOrDefault() ?? "";
        await users.UpdateOneAsync(
            session,
            UserFilters.ById(user.Id),
            Builders<PoleUser>.Update
                .Set(item => item.DefaultTenantId, defaultTenant)
                .Inc(item => item.MembershipFence, 1),
            cancellationToken: cancellationToken);
    }
}
