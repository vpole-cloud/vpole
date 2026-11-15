using MongoDB.Driver;
using Pole.Database;
using Pole.Encryption;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Tenants.Application.Endpoints.Invitations;
using Pole.Features.Tenants.Application.Mongo;
using Pole.Features.Tenants.Domain;
using Pole.Features.Tenants.Events;

namespace Pole.Features.Tenants.Application.Invitations;

public sealed record ManageInvitationRequest(
    string TenantId,
    string InvitationId,
    long ExpectedRevision,
    InvitationActor Actor
);

public sealed class ManageInvitation(
    IMongoCollection<Tenant> tenants,
    IMongoCollection<TenantInvitation> invitations,
    IEnvelopeEncryptionService encryption,
    StoreOutboxWrites storeOutboxWrites,
    MongoRunTransaction transaction,
    TimeProvider timeProvider
)
{
    public Task<RevokeTenantInvitationOutcome> RevokeAsync(ManageInvitationRequest request, CancellationToken ct) =>
        transaction.InvokeOutcomeAsync<RevokeTenantInvitationOutcome>(
            async (context, cancellationToken) =>
            {
                var tenant = await tenants.Find(context.Session, TenantFilters.ById(request.TenantId))
                    .SingleOrDefaultAsync(cancellationToken);
                if (tenant is null || !InvitationIdentity.CanGrant(tenant, request.Actor, TenantRole.Viewer))
                    return context.Abort(new InvitationForbidden());

                var invitation = await invitations.Find(
                        context.Session,
                        InvitationFilters.ForTenantById(tenant.Id, request.InvitationId))
                    .SingleOrDefaultAsync(cancellationToken);
                if (invitation is null)
                    return context.Abort(new InvitationNotFound());
                if (!InvitationIdentity.CanGrant(tenant, request.Actor, invitation.Role))
                    return context.Abort(new InvitationForbidden());
                if (invitation.Revision != request.ExpectedRevision)
                    return context.Abort(new InvitationRevisionConflict());
                if (invitation.Status != TenantInvitationStatus.Pending)
                    return context.Abort(new InvitationUnavailable());
                await tenants.UpdateOneAsync(
                    context.Session,
                    TenantFilters.ById(tenant.Id),
                    Builders<Tenant>.Update.Inc(item => item.ReferenceFence, 1),
                    cancellationToken: cancellationToken);
                var revoked = invitation with { Status = TenantInvitationStatus.Revoked };
                var replaced = await invitations.ReplaceOneAsync(
                    context.Session,
                    InvitationFilters.ForTenantAtRevision(tenant.Id, invitation.Id, request.ExpectedRevision),
                    revoked,
                    cancellationToken: cancellationToken);
                if (replaced.MatchedCount == 0)
                    return context.Abort(new InvitationRevisionConflict());
                await storeOutboxWrites(context.Session, [Changed(tenant.Id)], cancellationToken);
                return context.Commit(InvitationDto.From(revoked));
            },
            ct);

    public Task<ResendTenantInvitationOutcome> ResendAsync(ManageInvitationRequest request, CancellationToken ct) =>
        RotateAsync(request, null, ct);

    public Task<ResendTenantInvitationOutcome> RotateAsync(
        ManageInvitationRequest request,
        TenantRole? requestedRole,
        CancellationToken ct) => transaction.InvokeOutcomeAsync<ResendTenantInvitationOutcome>(
        async (context, cancellationToken) =>
        {
            var tenant = await tenants.Find(context.Session, TenantFilters.ById(request.TenantId))
                .SingleOrDefaultAsync(cancellationToken);
            if (tenant is null || !InvitationIdentity.CanGrant(tenant, request.Actor, TenantRole.Viewer))
                return context.Abort(new InvitationForbidden());

            var invitation = await invitations.Find(
                    context.Session,
                    InvitationFilters.ForTenantById(tenant.Id, request.InvitationId))
                .SingleOrDefaultAsync(cancellationToken);
            if (invitation is null)
                return context.Abort(new InvitationNotFound());
            var role = requestedRole ?? invitation.Role;
            if (!InvitationIdentity.CanGrant(tenant, request.Actor, invitation.Role) ||
                !InvitationIdentity.CanGrant(tenant, request.Actor, role))
                return context.Abort(new InvitationForbidden());
            if (invitation.Revision != request.ExpectedRevision)
                return context.Abort(new InvitationRevisionConflict());
            if (invitation.Status != TenantInvitationStatus.Pending)
                return context.Abort(new InvitationUnavailable());
            var token = InvitationIdentity.CreateToken();
            var revision = invitation.Revision + 1;
            var protectedToken = await encryption.EncryptStringAsync(
                token,
                EnvelopeEncryptionContext.ForTenant(
                    tenant.Id,
                    "TenantInvitation",
                    invitation.Id,
                    "delivery-token",
                    revision),
                cancellationToken);
            if (protectedToken is not EncryptedValue encrypted)
                return context.Abort(new InvitationEncryptionUnavailable());
            var updated = invitation with
            {
                Role = role, IssuerId = request.Actor.Subject, TokenHash = InvitationIdentity.Hash(token),
                DeliveryToken = encrypted, Revision = revision,
                ExpiresAtUtc = timeProvider.GetUtcNow().UtcDateTime + TenantInvitation.Lifetime,
                DeliveryStatus = InvitationDeliveryStatus.Pending,
            };
            await tenants.UpdateOneAsync(
                context.Session,
                TenantFilters.ById(tenant.Id),
                Builders<Tenant>.Update.Inc(item => item.ReferenceFence, 1),
                cancellationToken: cancellationToken);
            var replaced = await invitations.ReplaceOneAsync(
                context.Session,
                InvitationFilters.ForTenantAtRevision(tenant.Id, invitation.Id, request.ExpectedRevision),
                updated,
                cancellationToken: cancellationToken);
            if (replaced.MatchedCount == 0)
                return context.Abort(new InvitationRevisionConflict());
            await storeOutboxWrites(
                context.Session,
                [
                    OutboxWrite.Create(
                        new TenantInvitationDeliveryRequested(updated.Id, revision, EventCore.Create(tenant.Id))),
                    Changed(tenant.Id)
                ],
                cancellationToken);
            return context.Commit(InvitationDto.From(updated));
        },
        ct);

    internal static OutboxWrite Changed(string tenantId) =>
        OutboxWrite.Create(new TenantMembersChangedEvent(EventCore.Create(tenantId)));
}
