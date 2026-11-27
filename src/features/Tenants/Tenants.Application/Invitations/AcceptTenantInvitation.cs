// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;
using Pole.Features.Tenants.Application.Endpoints.Invitations;
using Pole.Database;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Tenants.Application.Common;
using Pole.Features.Tenants.Application.Mongo;
using Pole.Features.Tenants.Domain;
using Pole.Features.Users.Contracts;

namespace Pole.Features.Tenants.Application.Invitations;

public sealed record AcceptTenantInvitation(
    string Token,
    string Subject,
    string Email,
    string? FirstName,
    string? LastName
)
{
    public override string ToString() => "AcceptTenantInvitation(redacted)";
}

public delegate Task<AcceptTenantInvitationHttpOutcome> PersistAcceptedTenantInvitation(
    AcceptTenantInvitation request,
    CancellationToken cancellationToken = default);

internal sealed class MongoAcceptTenantInvitation(
    IMongoCollection<Tenant> tenants,
    IMongoCollection<TenantInvitation> invitations,
    IMongoCollection<TenantInvitationAcceptance> receipts,
    JoinInvitedUser joinInvitedUser,
    StoreOutboxWrites storeOutboxWrites,
    MongoRunTransaction transaction,
    TimeProvider timeProvider
)
{
    public Task<AcceptTenantInvitationHttpOutcome> InvokeAsync(
        AcceptTenantInvitation request,
        CancellationToken cancellationToken = default)
    {
        return transaction.InvokeOutcomeAsync<AcceptTenantInvitationHttpOutcome>(
            async (context, ct) =>
            {
                var hash = InvitationIdentity.Hash(request.Token);
                var email = InvitationIdentity.NormalizeEmail(request.Email);
                var receipt = await receipts.Find(context.Session, InvitationFilters.Receipt(hash))
                    .SingleOrDefaultAsync(ct);
                if (receipt is not null)
                {
                    if (receipt.Subject != request.Subject || receipt.Email != email)
                        return context.Abort(new InvitationIdentityMismatch());
                    var currentTenants = await tenants.Find(
                            context.Session,
                            TenantFilters.ForMemberUser(request.Subject))
                        .ToListAsync(ct);
                    var retainedMembership = currentTenants.Any(item => item.Id == receipt.TenantId);
                    var currentDefault = currentTenants.FirstOrDefault(item => item.Members.Any(member =>
                            member.UserId == request.Subject && member.IsDefault))
                        ?.Id ?? "";
                    return context.Abort(
                        retainedMembership
                            ? (AcceptTenantInvitationHttpOutcome)new InvitationAcceptedDto(
                                receipt.TenantId,
                                currentDefault)
                            : new InvitationUnavailable());
                }

                var invitation = await invitations.Find(context.Session, InvitationFilters.ByTokenHash(hash))
                    .SingleOrDefaultAsync(ct);
                var now = timeProvider.GetUtcNow().UtcDateTime;
                if (invitation is null || invitation.Status != TenantInvitationStatus.Pending ||
                    invitation.ExpiresAtUtc <= now)
                    return context.Abort(new InvitationUnavailable());
                if (invitation.Email != email || (invitation.OnboardingSubject is not null &&
                        invitation.OnboardingSubject != request.Subject))
                    return context.Abort(new InvitationIdentityMismatch());

                var tenant = await tenants.Find(context.Session, TenantFilters.ById(invitation.TenantId))
                    .SingleOrDefaultAsync(ct);
                var issuer = new InvitationActor(invitation.IssuerId);
                if (tenant is null || !InvitationIdentity.CanGrant(tenant, issuer, invitation.Role))
                    return context.Abort(new InvitationUnavailable());

                var joined = await joinInvitedUser(
                    new JoinInvitedUserRequest(request.Subject, email, tenant.Id, request.FirstName, request.LastName),
                    context.Session,
                    ct);
                var defaultTenantId = joined switch
                {
                    InvitedUserJoined userJoined => userJoined.DefaultTenantId,
                    InvitedUserIdentityConflict => null,
                };
                if (defaultTenantId is null)
                    return context.Abort(new InvitationIdentityConflict());

                var member = tenant.Members.FirstOrDefault(item => item.UserId == request.Subject);
                if (member is null)
                {
                    member = TenantFactory.CreateMember(
                        tenant.Id,
                        request.Subject,
                        email,
                        invitation.Role,
                        defaultTenantId == tenant.Id);
                    await tenants.UpdateOneAsync(
                        context.Session,
                        TenantFilters.ById(tenant.Id),
                        TenantUpdates.AddMember(member),
                        cancellationToken: ct);
                    await storeOutboxWrites(
                        context.Session,
                        [TenantAccessEventExtensions.MembershipChanged(tenant.Id, request.Subject)],
                        ct);
                }
                else
                {
                    await tenants.UpdateOneAsync(
                        context.Session,
                        TenantFilters.ById(tenant.Id),
                        Builders<Tenant>.Update.Inc(item => item.ReferenceFence, 1),
                        cancellationToken: ct);
                }

                await invitations.UpdateOneAsync(
                    context.Session,
                    InvitationFilters.AtRevision(invitation.Id, invitation.Revision),
                    Builders<TenantInvitation>.Update.Set(item => item.Status, TenantInvitationStatus.Accepted),
                    cancellationToken: ct);
                await receipts.InsertOneAsync(
                    context.Session,
                    new TenantInvitationAcceptance(
                        hash,
                        tenant.Id,
                        request.Subject,
                        email,
                        member.Id,
                        defaultTenantId,
                        now),
                    cancellationToken: ct);
                return context.Commit(new InvitationAcceptedDto(tenant.Id, defaultTenantId));
            },
            cancellationToken);
    }
}
