using MongoDB.Driver;
using Pole.Features.Tenants.Application.Endpoints.Invitations;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Tenants.Application.Mongo;
using Pole.Features.Tenants.Domain;
using Pole.Features.Tenants.Events;

namespace Pole.Features.Tenants.Application.Invitations;

public sealed record CreateTenantInvitation(
    string TenantId,
    string Email,
    TenantRole Role,
    InvitationActor Actor
);

public delegate Task<CreateTenantInvitationOutcome> PersistTenantInvitation(
    CreateTenantInvitation request,
    CancellationToken cancellationToken = default);

internal sealed class MongoPersistTenantInvitation(
    IMongoCollection<Tenant> tenants,
    IMongoCollection<TenantInvitation> invitations,
    IEnvelopeEncryptionService encryption,
    StoreOutboxWrites storeOutboxWrites,
    MongoRunTransaction transaction,
    TimeProvider timeProvider
)
{
    public Task<CreateTenantInvitationOutcome> InvokeAsync(
        CreateTenantInvitation request,
        CancellationToken cancellationToken = default) => transaction.InvokeOutcomeAsync<CreateTenantInvitationOutcome>(
        async (context, ct) =>
        {
            var tenant = await tenants.Find(context.Session, TenantFilters.ById(request.TenantId))
                .SingleOrDefaultAsync(ct);
            if (tenant is null)
                return context.Abort(new TenantNotFound(request.TenantId));
            if (!InvitationIdentity.CanGrant(tenant, request.Actor, request.Role))
                return context.Abort(new InvitationForbidden());

            var email = InvitationIdentity.NormalizeEmail(request.Email);
            if (tenant.Members.Any(member => InvitationIdentity.NormalizeEmail(member.Email) == email))
                return context.Abort(new InvitationRecipientAlreadyMember());
            var id = InvitationIdentity.Slot(tenant.Id, email);
            var existing = await invitations.Find(context.Session, InvitationFilters.ById(id)).SingleOrDefaultAsync(ct);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (existing is { Status: TenantInvitationStatus.Pending } && existing.ExpiresAtUtc > now)
                return context.Abort(new InvitationAlreadyPending());

            var revision = (existing?.Revision ?? 0) + 1;
            var token = InvitationIdentity.CreateToken();
            var protectedToken = await encryption.EncryptStringAsync(
                token,
                EnvelopeEncryptionContext.ForTenant(tenant.Id, "TenantInvitation", id, "delivery-token", revision),
                ct);
            if (protectedToken is not EncryptedValue encrypted)
                return context.Abort(new InvitationEncryptionUnavailable());
            var invitation = TenantInvitation.CreatePending(
                id,
                tenant.Id,
                email,
                request.Role,
                request.Actor.Subject,
                InvitationIdentity.Hash(token),
                encrypted,
                revision,
                now);

            // Any concurrent issuer downgrade/removal conflicts with this same tenant write.
            await tenants.UpdateOneAsync(
                context.Session,
                TenantFilters.ById(tenant.Id),
                Builders<Tenant>.Update.Inc(item => item.ReferenceFence, 1),
                cancellationToken: ct);
            await invitations.ReplaceOneAsync(
                context.Session,
                InvitationFilters.ById(id),
                invitation,
                new ReplaceOptions { IsUpsert = true },
                ct);
            await storeOutboxWrites(
                context.Session,
                [
                    OutboxWrite.Create(
                        new TenantInvitationDeliveryRequested(id, revision, EventCore.Create(tenant.Id))),
                    ManageInvitation.Changed(tenant.Id)
                ],
                ct);
            return context.Commit(InvitationDto.From(invitation));
        },
        cancellationToken);
}
