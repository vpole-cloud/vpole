using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Tenants.Application.Endpoints.Invitations;
using Pole.Features.Tenants.Application.Mongo;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Contracts;

namespace Pole.Features.Tenants.Application.Invitations;

public sealed class StartInvitationOnboarding(
    MongoLoadInvitation load,
    StartInvitedIdentity startIdentity,
    InvitationLinks links,
    IMongoCollection<TenantInvitation> invitations,
    IMongoCollection<Tenant> tenants,
    MongoRunTransaction transaction,
    TimeProvider timeProvider
)
{
    public async Task<StartInvitationOnboardingOutcome> InvokeAsync(string token, CancellationToken ct)
    {
        var invitation = await load.ValidAsync(token, ct);
        if (invitation is null)
            return new InvitationUnavailable();
        var identity = await startIdentity(
            new StartInvitedIdentityRequest(invitation.Email, links.SetupCallback()),
            ct);
        if (identity is InvitedIdentityUnavailable)
            return new InvitationIdentitySetupUnavailable();
        if (identity is not InvitedIdentityStarted started)
            throw new InvalidOperationException("Unexpected invitation identity outcome.");
        return await transaction.InvokeOutcomeAsync<StartInvitationOnboardingOutcome>(
            async (context, cancellationToken) =>
            {
                var current = await invitations.Find(context.Session, InvitationFilters.ById(invitation.Id))
                    .SingleOrDefaultAsync(cancellationToken);
                if (current is null || current.Revision != invitation.Revision ||
                    current.TokenHash != invitation.TokenHash || current.Status != TenantInvitationStatus.Pending ||
                    current.ExpiresAtUtc <= timeProvider.GetUtcNow().UtcDateTime)
                    return context.Abort(new InvitationUnavailable());
                var tenant = await tenants.Find(context.Session, TenantFilters.ById(current.TenantId))
                    .SingleOrDefaultAsync(cancellationToken);
                if (tenant is null || !InvitationIdentity.CanGrant(
                        tenant,
                        new InvitationActor(current.IssuerId),
                        current.Role))
                    return context.Abort(new InvitationUnavailable());
                if (current.OnboardingSubject is not null && current.OnboardingSubject != started.Subject)
                    return context.Abort(new InvitationUnavailable());
                await tenants.UpdateOneAsync(
                    context.Session,
                    TenantFilters.ById(tenant.Id),
                    Builders<Tenant>.Update.Inc(item => item.ReferenceFence, 1),
                    cancellationToken: cancellationToken);
                await invitations.UpdateOneAsync(
                    context.Session,
                    InvitationFilters.AtRevision(current.Id, current.Revision),
                    Builders<TenantInvitation>.Update.Set(item => item.OnboardingSubject, started.Subject),
                    cancellationToken: cancellationToken);
                return context.Commit(new InvitationOnboardingDto(started.SetupEmailSent));
            },
            ct);
    }
}

public sealed record InvitationOnboardingDto(
    bool SetupEmailSent
);

public sealed record InvitationIdentitySetupUnavailable;
