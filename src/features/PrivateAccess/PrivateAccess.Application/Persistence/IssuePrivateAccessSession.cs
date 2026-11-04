using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Outbox.Contracts;
using Pole.Features.PrivateAccess.Application.Common;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;
using Pole.Features.PrivateAccess.Events;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Tenants.Contracts;
using Pole.Workloads;

namespace Pole.Features.PrivateAccess.Application.Persistence;

public sealed record PrivateAccessSessionSubjectMismatch;

public sealed record PrivateAccessSessionIssued(
    string SessionId,
    string Ticket,
    DateTime ExpiresAtUtc)
{
    public override string ToString() =>
        $"PrivateAccessSessionIssued {{ SessionId = {SessionId}, Ticket = [redacted], ExpiresAtUtc = {ExpiresAtUtc:O} }}";
}

public union IssuePrivateAccessSessionOutcome(
    PrivateAccessSessionIssued,
    PrivateAccessGrantUnavailable,
    PrivateAccessSessionSubjectMismatch,
    PrivateAccessSubjectMembershipRequired,
    PrivateAccessDeploymentUnavailable,
    PrivateAccessEndpointUnavailable,
    PrivateAccessBindingChanged);

public delegate Task<IssuePrivateAccessSessionOutcome> IssuePrivateAccessSession(
    string tenantId,
    string grantId,
    string subjectUserId,
    CancellationToken cancellationToken = default);

internal sealed class MongoIssuePrivateAccessSession(
    RunMongoOutcomeTransaction<IssuePrivateAccessSessionOutcome> transactions,
    IMongoCollection<PrivateAccessGrant> grants,
    IMongoCollection<PrivateAccessSession> sessions,
    IMongoCollection<PrivateAccessAudit> audit,
    LoadTenantMember loadTenantMember,
    FenceRegisteredResource fenceResource,
    LoadExactResourceDeploymentAccessPlacement loadPlacement,
    PrivateAccessEndpointCatalog endpointCatalog,
    PrivateAccessAdmission admission,
    ApplyDeploymentTargetReferenceChanges targetReferences,
    StoreOutboxWrites storeOutboxWrites,
    TimeProvider timeProvider)
{
    public Task<IssuePrivateAccessSessionOutcome> InvokeAsync(
        string tenantId,
        string grantId,
        string subjectUserId,
        CancellationToken cancellationToken = default) =>
        transactions(async (transaction, ct) =>
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var grant = await grants.FindOneAndUpdateAsync(
                transaction.Session,
                PrivateAccessFilters.Grant(tenantId, grantId),
                Builders<PrivateAccessGrant>.Update.Inc(item => item.ReferenceFence, 1),
                new FindOneAndUpdateOptions<PrivateAccessGrant> { ReturnDocument = ReturnDocument.After },
                ct);
            if (grant is null || !grant.IsActive(now))
                return await DenyAsync(new PrivateAccessGrantUnavailable(), PrivateAccessAuditReason.GrantUnavailable);
            if (grant.SubjectUserId != subjectUserId)
                return await DenyAsync(new PrivateAccessSessionSubjectMismatch(), PrivateAccessAuditReason.SubjectMismatch);

            var member = await loadTenantMember(
                new LoadTenantMemberRequest(
                    TenantId: tenantId,
                    UserId: subjectUserId),
                session: transaction.Session,
                cancellationToken: ct);
            if (member is not TenantMemberSnapshot)
                return await DenyAsync(new PrivateAccessSubjectMembershipRequired(), PrivateAccessAuditReason.MembershipRequired);

            var resourceType = ResourceTypeId.Create(grant.ResourceType);
            var registered = await fenceResource(
                new FenceRegisteredResourceRequest(
                    TenantId: tenantId,
                    ResourceTypeId: resourceType,
                    ResourceId: grant.ResourceId),
                session: transaction.Session,
                cancellationToken: ct);
            if (registered is null)
                return await DenyAsync(new PrivateAccessDeploymentUnavailable(), PrivateAccessAuditReason.DeploymentUnavailable);
            var placement = await loadPlacement(
                new LoadExactResourceDeploymentAccessPlacementRequest(
                    TenantId: tenantId,
                    ResourceType: resourceType,
                    ResourceId: grant.ResourceId,
                    DeploymentId: grant.ResourceDeploymentId),
                session: transaction.Session,
                cancellationToken: ct);
            if (placement is null ||
                placement.ResourceGroupId != registered.ResourceGroupId ||
                placement.Status != ResourceDeploymentStatus.Applied ||
                placement.DesiredOperation != ResourceDeploymentDesiredOperation.Apply)
                return await DenyAsync(new PrivateAccessDeploymentUnavailable(), PrivateAccessAuditReason.DeploymentUnavailable);
            var endpoint = await endpointCatalog.ResolveAsync(
                tenantId, resourceType, grant.ResourceId, grant.EndpointId,
                transaction.Session, ct);
            if (endpoint is null)
                return await DenyAsync(new PrivateAccessEndpointUnavailable(), PrivateAccessAuditReason.EndpointUnavailable);

            var (rawTicket, ticketHash) = PrivateAccessTicketCodec.Issue();
            var ticketExpiresAt = now.AddSeconds(30);
            if (ticketExpiresAt > grant.ExpiresAtUtc)
                ticketExpiresAt = grant.ExpiresAtUtc;
            var accessSession = PrivateAccessSessionFactory.Create(
                tenantId, subjectUserId,
                new PrivateAccessSessionGrantSnapshot(
                    grant.Id, grant.Revision, grant.ResourceType, grant.ResourceId,
                    grant.ResourceDeploymentId, grant.EndpointId),
                new PrivateAccessSessionTicket(ticketHash, ticketExpiresAt),
                new PrivateAccessSessionRouteSnapshot(
                    placement.DeploymentTargetId, placement.RuntimeCellId,
                    placement.IsolationScopeId, placement.KubernetesNamespace,
                    endpoint.ServiceName, endpoint.ServicePort, endpoint.TargetPort,
                    placement.DeploymentRevision, placement.DesiredGeneration),
                grant.ExpiresAtUtc, now);

            var checkedAccess = await admission.CheckAsync(
                accessSession, transaction.Session, ct);
            if (checkedAccess is not PrivateAccessLeaseSnapshot)
            {
                return checkedAccess switch
                {
                    PrivateAccessGrantUnavailable error => await DenyAsync(error, PrivateAccessAuditReason.GrantUnavailable),
                    PrivateAccessSubjectMembershipRequired error => await DenyAsync(error, PrivateAccessAuditReason.MembershipRequired),
                    PrivateAccessDeploymentUnavailable error => await DenyAsync(error, PrivateAccessAuditReason.DeploymentUnavailable),
                    PrivateAccessEndpointUnavailable error => await DenyAsync(error, PrivateAccessAuditReason.EndpointUnavailable),
                    PrivateAccessBindingChanged error => await DenyAsync(error, PrivateAccessAuditReason.BindingChanged),
                    PrivateAccessLeaseSnapshot => throw new InvalidOperationException()
                };
            }

            var reference = await targetReferences(
                new ApplyDeploymentTargetReferenceChangesRequest(
                    Owner: new DeploymentTargetReferenceOwner(
                    DeploymentTargetReferenceOwnerTypes.PrivateAccessSession,
                    accessSession.Id),
                    AddedDeploymentTargetIds: [accessSession.Route.DeploymentTargetId],
                    RemovedDeploymentTargetIds: []),
                session: transaction.Session,
                cancellationToken: ct);
            if (reference is DeploymentTargetReferenceChangeRejected)
                return await DenyAsync(new PrivateAccessDeploymentUnavailable(), PrivateAccessAuditReason.DeploymentUnavailable);

            await sessions.InsertOneAsync(transaction.Session, accessSession,
                cancellationToken: ct);
            await audit.InsertOneAsync(transaction.Session, PrivateAccessAuditFactory.Create(
                tenantId, grant.Id,
                accessSession.Id, subjectUserId, now, new PrivateAccessSessionIssuedAudit()),
                cancellationToken: ct);
            await storeOutboxWrites(transaction.Session,
                [OutboxWrite.Create(new PrivateAccessSessionIssuedEvent(
                    grant.Id, accessSession.Id, subjectUserId)
                {
                    EventCore = EventCore.Create(tenantId)
                })], ct);
            return transaction.Commit(new IssuePrivateAccessSessionOutcome(
                new PrivateAccessSessionIssued(accessSession.Id, rawTicket, ticketExpiresAt)));

            async Task<MongoTransactionDecision<IssuePrivateAccessSessionOutcome>> DenyAsync(
                IssuePrivateAccessSessionOutcome denied,
                PrivateAccessAuditReason reason)
            {
                await audit.InsertOneAsync(transaction.Session, PrivateAccessAuditFactory.Create(
                    tenantId, grantId,
                    null, subjectUserId, now, new PrivateAccessSessionIssueDeniedAudit(reason)),
                    cancellationToken: ct);
                return transaction.Commit(denied);
            }
        }, cancellationToken);
}
