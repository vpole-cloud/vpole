using MongoDB.Driver;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Tenants.Application.Mongo;
using Pole.Features.Tenants.Domain;
using Pole.Features.Tenants.Events;

namespace Pole.Features.Tenants.Application.Invitations;

public sealed class DeliverTenantInvitation(
    IMongoCollection<TenantInvitation> invitations,
    IMongoCollection<Tenant> tenants,
    IEnvelopeEncryptionService encryption,
    MongoLoadInvitation load,
    InvitationLinks links,
    SendInvitationMail sendMail,
    StoreOutboxWrites storeOutboxWrites,
    MongoRunTransaction transaction,
    TimeProvider timeProvider
)
{
    public async Task InvokeAsync(TenantInvitationDeliveryRequested message, CancellationToken ct)
    {
        var invitation = await invitations
            .Find(
                InvitationFilters.ForTenantAtRevision(
                    message.EventCore.TenantId,
                    message.InvitationId,
                    message.Revision))
            .SingleOrDefaultAsync(ct);
        if (invitation is null || invitation.Status != TenantInvitationStatus.Pending ||
            invitation.DeliveryStatus == InvitationDeliveryStatus.Sent ||
            invitation.ExpiresAtUtc <= timeProvider.GetUtcNow().UtcDateTime)
            return;
        var tenant = await tenants.Find(TenantFilters.ById(invitation.TenantId)).SingleOrDefaultAsync(ct);
        if (tenant is null || !InvitationIdentity.CanGrant(
                tenant,
                new InvitationActor(invitation.IssuerId),
                invitation.Role))
        {
            await RecordAsync(invitation, InvitationDeliveryStatus.Failed, ct);
            return;
        }

        try
        {
            var decrypted = await encryption.DecryptStringAsync(
                invitation.DeliveryToken,
                EnvelopeEncryptionContext.ForTenant(
                    invitation.TenantId,
                    "TenantInvitation",
                    invitation.Id,
                    "delivery-token",
                    invitation.Revision),
                ct);
            if (decrypted is not string token)
            {
                await RecordAsync(invitation, InvitationDeliveryStatus.Failed, ct);
                return;
            }

            var current = await load.ValidAsync(token, ct);
            if (current is null || current.Revision != invitation.Revision)
            {
                await RecordAsync(invitation, InvitationDeliveryStatus.Failed, ct);
                return;
            }

            // SMTP is at-least-once. A message already in flight may arrive after revocation;
            // every subsequent preview/onboarding/accept checks the live revision and authority.
            await sendMail(
                new InvitationMail(invitation.Email, tenant.Name, links.Acceptance(token), invitation.ExpiresAtUtc),
                ct);
            await RecordAsync(invitation, InvitationDeliveryStatus.Sent, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            await RecordAsync(invitation, InvitationDeliveryStatus.Failed, ct);
            throw;
        }
    }

    private Task RecordAsync(
        TenantInvitation invitation,
        InvitationDeliveryStatus status,
        CancellationToken ct) => transaction.InvokeOutcomeAsync<bool>(
        async (context, cancellationToken) =>
        {
            var result = await invitations.UpdateOneAsync(
                context.Session,
                InvitationFilters.UnsentAtRevision(invitation.TenantId, invitation.Id, invitation.Revision),
                Builders<TenantInvitation>.Update.Set(item => item.DeliveryStatus, status),
                cancellationToken: cancellationToken);
            if (result.ModifiedCount == 0)
                return context.Abort(false);
            await storeOutboxWrites(
                context.Session,
                [ManageInvitation.Changed(invitation.TenantId)],
                cancellationToken);
            return context.Commit(true);
        },
        ct);
}
