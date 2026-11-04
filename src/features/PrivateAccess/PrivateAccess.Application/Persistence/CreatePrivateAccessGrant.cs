using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.PrivateAccess.Domain;
using Pole.Features.PrivateAccess.Events;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Tenants.Contracts;
using Pole.Workloads;

namespace Pole.Features.PrivateAccess.Application.Persistence;

public sealed record CreatePrivateAccessGrantCommand(
    string TenantId,
    string ActorUserId,
    string SubjectUserId,
    ResourceTypeId ResourceType,
    string ResourceId,
    string ResourceDeploymentId,
    string EndpointId,
    DateTime ExpiresAtUtc);

public sealed record PrivateAccessManageRoleRequired;
public sealed record PrivateAccessSubjectNotMember;
public sealed record PrivateAccessResourceUnavailable;
public sealed record PrivateAccessEndpointUnsupported;
public sealed record PrivateAccessGrantExpiryInvalid;

public union CreatePrivateAccessGrantPersistenceOutcome(
    PrivateAccessGrant,
    PrivateAccessManageRoleRequired,
    PrivateAccessSubjectNotMember,
    PrivateAccessResourceUnavailable,
    PrivateAccessEndpointUnsupported,
    PrivateAccessGrantExpiryInvalid);

public delegate Task<CreatePrivateAccessGrantPersistenceOutcome> PersistPrivateAccessGrant(
    CreatePrivateAccessGrantCommand command,
    CancellationToken cancellationToken = default);

internal sealed class MongoCreatePrivateAccessGrant(
    RunMongoOutcomeTransaction<CreatePrivateAccessGrantPersistenceOutcome> transactions,
    LoadTenantMember loadTenantMember,
    FenceRegisteredResource fenceResource,
    LoadExactResourceDeploymentAccessPlacement loadPlacement,
    PrivateAccessEndpointCatalog endpointCatalog,
    IMongoCollection<PrivateAccessGrant> grants,
    IMongoCollection<PrivateAccessAudit> audit,
    StoreOutboxWrites storeOutboxWrites,
    TimeProvider timeProvider)
{
    public Task<CreatePrivateAccessGrantPersistenceOutcome> InvokeAsync(
        CreatePrivateAccessGrantCommand command,
        CancellationToken cancellationToken = default) =>
        transactions(async (transaction, ct) =>
        {
            var actor = await loadTenantMember(
                new LoadTenantMemberRequest(
                    TenantId: command.TenantId,
                    UserId: command.ActorUserId),
                session: transaction.Session,
                cancellationToken: ct);
            if (actor is not TenantMemberSnapshot { Role: >= TenantMembershipRole.Admin })
                return transaction.Abort(new CreatePrivateAccessGrantPersistenceOutcome(
                    new PrivateAccessManageRoleRequired()));

            var subject = await loadTenantMember(
                new LoadTenantMemberRequest(
                    TenantId: command.TenantId,
                    UserId: command.SubjectUserId),
                session: transaction.Session,
                cancellationToken: ct);
            if (subject is not TenantMemberSnapshot)
                return transaction.Abort(new CreatePrivateAccessGrantPersistenceOutcome(
                    new PrivateAccessSubjectNotMember()));

            var registered = await fenceResource(
                new FenceRegisteredResourceRequest(
                    TenantId: command.TenantId,
                    ResourceTypeId: command.ResourceType,
                    ResourceId: command.ResourceId),
                session: transaction.Session,
                cancellationToken: ct);
            if (registered is null)
                return transaction.Abort(new CreatePrivateAccessGrantPersistenceOutcome(
                    new PrivateAccessResourceUnavailable()));

            var placement = await loadPlacement(
                new LoadExactResourceDeploymentAccessPlacementRequest(
                    TenantId: command.TenantId,
                    ResourceType: command.ResourceType,
                    ResourceId: command.ResourceId,
                    DeploymentId: command.ResourceDeploymentId),
                session: transaction.Session,
                cancellationToken: ct);
            if (placement is null ||
                placement.ResourceGroupId != registered.ResourceGroupId ||
                placement.Status != ResourceDeploymentStatus.Applied ||
                placement.DesiredOperation != ResourceDeploymentDesiredOperation.Apply)
            {
                return transaction.Abort(new CreatePrivateAccessGrantPersistenceOutcome(
                    new PrivateAccessResourceUnavailable()));
            }

            var endpoint = await endpointCatalog.ResolveAsync(
                command.TenantId, command.ResourceType, command.ResourceId,
                command.EndpointId, transaction.Session, ct);
            if (endpoint is null)
                return transaction.Abort(new CreatePrivateAccessGrantPersistenceOutcome(
                    new PrivateAccessEndpointUnsupported()));

            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (command.ExpiresAtUtc <= now)
                return transaction.Abort(new CreatePrivateAccessGrantPersistenceOutcome(
                    new PrivateAccessGrantExpiryInvalid()));
            var grant = PrivateAccessGrantFactory.Create(
                command.TenantId, command.SubjectUserId, command.ResourceType.Value,
                command.ResourceId, command.ResourceDeploymentId, command.EndpointId,
                command.ActorUserId, now, command.ExpiresAtUtc);
            await grants.InsertOneAsync(transaction.Session, grant, cancellationToken: ct);
            await audit.InsertOneAsync(transaction.Session, PrivateAccessAuditFactory.Create(
                command.TenantId,
                grant.Id, null, command.ActorUserId, now, new PrivateAccessGrantCreatedAudit()),
                cancellationToken: ct);
            await storeOutboxWrites(transaction.Session,
                [OutboxWrite.Create(new PrivateAccessGrantCreatedEvent(
                    grant.Id, grant.SubjectUserId, grant.ResourceDeploymentId, grant.EndpointId)
                {
                    EventCore = EventCore.Create(command.TenantId)
                })], ct);
            return transaction.Commit(new CreatePrivateAccessGrantPersistenceOutcome(grant));
        }, cancellationToken);
}
