using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Outbox.Contracts;
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
                return await DenyAsync(new PrivateAccessGrantUnavailable(), "GrantUnavailable");
            if (grant.SubjectUserId != subjectUserId)
                return await DenyAsync(new PrivateAccessSessionSubjectMismatch(), "SubjectMismatch");

            var member = await loadTenantMember(tenantId, subjectUserId, transaction.Session, ct);
            if (member is not TenantMemberSnapshot)
                return await DenyAsync(new PrivateAccessSubjectMembershipRequired(), "MembershipRequired");

            var resourceType = ResourceTypeId.Create(grant.ResourceType);
            var registered = await fenceResource(
                tenantId, resourceType, grant.ResourceId, transaction.Session, ct);
            if (registered is null)
                return await DenyAsync(new PrivateAccessDeploymentUnavailable(), "DeploymentUnavailable");
            var placement = await loadPlacement(
                tenantId, resourceType, grant.ResourceId, grant.ResourceDeploymentId,
                transaction.Session, ct);
            if (placement is null ||
                placement.ResourceGroupId != registered.ResourceGroupId ||
                placement.Status != "Applied" ||
                placement.DesiredOperation != "Apply")
                return await DenyAsync(new PrivateAccessDeploymentUnavailable(), "DeploymentUnavailable");
            var endpoint = await endpointCatalog.ResolveAsync(
                tenantId, resourceType, grant.ResourceId, grant.EndpointId,
                transaction.Session, ct);
            if (endpoint is null)
                return await DenyAsync(new PrivateAccessEndpointUnavailable(), "EndpointUnavailable");

            var rawTicket = $"pole_pa_{Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()}";
            var ticketHash = HashTicket(rawTicket);
            var ticketExpiresAt = now.AddSeconds(30);
            if (ticketExpiresAt > grant.ExpiresAtUtc)
                ticketExpiresAt = grant.ExpiresAtUtc;
            var accessSession = new PrivateAccessSession(
                ObjectId.GenerateNewId().ToString(), tenantId, subjectUserId,
                grant.Id, grant.Revision, ticketHash, ticketExpiresAt,
                grant.ExpiresAtUtc, now, 1, new PrivateAccessTicketPending(),
                grant.ResourceType, grant.ResourceId, grant.ResourceDeploymentId,
                grant.EndpointId, placement.DeploymentTargetId,
                placement.RuntimeCellId, placement.IsolationScopeId,
                placement.KubernetesNamespace, endpoint.ServiceName,
                endpoint.ServicePort, endpoint.TargetPort,
                placement.DeploymentRevision, placement.DesiredGeneration, null);

            var checkedAccess = await admission.CheckAsync(
                accessSession, transaction.Session, ct);
            if (checkedAccess is not PrivateAccessLeaseSnapshot)
            {
                return checkedAccess switch
                {
                    PrivateAccessGrantUnavailable error => await DenyAsync(error, "GrantUnavailable"),
                    PrivateAccessSubjectMembershipRequired error => await DenyAsync(error, "MembershipRequired"),
                    PrivateAccessDeploymentUnavailable error => await DenyAsync(error, "DeploymentUnavailable"),
                    PrivateAccessEndpointUnavailable error => await DenyAsync(error, "EndpointUnavailable"),
                    PrivateAccessBindingChanged error => await DenyAsync(error, "BindingChanged"),
                    PrivateAccessLeaseSnapshot => throw new InvalidOperationException()
                };
            }

            var reference = await targetReferences(
                new DeploymentTargetReferenceOwner(
                    DeploymentTargetReferenceOwnerTypes.PrivateAccessSession,
                    accessSession.Id),
                [accessSession.DeploymentTargetId],
                [],
                transaction.Session,
                ct);
            if (reference is DeploymentTargetReferenceChangeRejected)
                return await DenyAsync(new PrivateAccessDeploymentUnavailable(), "DeploymentUnavailable");

            await sessions.InsertOneAsync(transaction.Session, accessSession,
                cancellationToken: ct);
            await audit.InsertOneAsync(transaction.Session, new PrivateAccessAudit(
                ObjectId.GenerateNewId().ToString(), tenantId, grant.Id,
                accessSession.Id, subjectUserId, "SessionIssued", now),
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
                string code)
            {
                await audit.InsertOneAsync(transaction.Session, new PrivateAccessAudit(
                    ObjectId.GenerateNewId().ToString(), tenantId, grantId,
                    null, subjectUserId, $"SessionIssueDenied:{code}", now),
                    cancellationToken: ct);
                return transaction.Commit(denied);
            }
        }, cancellationToken);

    internal static string HashTicket(string ticket) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}
