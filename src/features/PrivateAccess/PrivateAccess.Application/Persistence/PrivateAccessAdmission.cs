using MongoDB.Driver;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Tenants.Contracts;
using Pole.Workloads;

namespace Pole.Features.PrivateAccess.Application.Persistence;

internal union CheckPrivateAccessSessionOutcome(
    PrivateAccessLeaseSnapshot,
    PrivateAccessGrantUnavailable,
    PrivateAccessSubjectMembershipRequired,
    PrivateAccessDeploymentUnavailable,
    PrivateAccessEndpointUnavailable,
    PrivateAccessBindingChanged);

internal sealed class PrivateAccessAdmission(
    IMongoCollection<PrivateAccessGrant> grants,
    LoadTenantMember loadTenantMember,
    FenceRegisteredResource fenceResource,
    LoadExactResourceDeploymentAccessPlacement loadPlacement,
    FenceDeploymentTargetPrivateAccess fenceTarget,
    PrivateAccessEndpointCatalog endpointCatalog,
    TimeProvider timeProvider)
{
    public async Task<CheckPrivateAccessSessionOutcome> CheckAsync(
        PrivateAccessSession accessSession,
        IClientSessionHandle transactionSession,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var grant = await grants.FindOneAndUpdateAsync(
            transactionSession,
            PrivateAccessFilters.Grant(accessSession.TenantId, accessSession.GrantId),
            Builders<PrivateAccessGrant>.Update.Inc(item => item.ReferenceFence, 1),
            new FindOneAndUpdateOptions<PrivateAccessGrant> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        if (grant is null || !grant.IsActive(now))
            return new PrivateAccessGrantUnavailable();
        if (PrivateAccessGrantBinding.From(grant) !=
            PrivateAccessGrantBinding.From(accessSession))
            return new PrivateAccessBindingChanged();

        var member = await loadTenantMember(
            accessSession.TenantId, accessSession.SubjectUserId, transactionSession,
            cancellationToken);
        if (member is not TenantMemberSnapshot)
            return new PrivateAccessSubjectMembershipRequired();

        var resourceType = ResourceTypeId.Create(accessSession.ResourceType);
        var registered = await fenceResource(
            accessSession.TenantId, resourceType, accessSession.ResourceId,
            transactionSession, cancellationToken);
        if (registered is null)
            return new PrivateAccessDeploymentUnavailable();
        var placement = await loadPlacement(
            accessSession.TenantId, resourceType, accessSession.ResourceId,
            accessSession.ResourceDeploymentId, transactionSession, cancellationToken);
        if (placement is null ||
            placement.ResourceGroupId != registered.ResourceGroupId ||
            placement.Status != "Applied" ||
            placement.DesiredOperation != "Apply")
            return new PrivateAccessDeploymentUnavailable();
        var target = await fenceTarget(
            placement.DeploymentTargetId, transactionSession, cancellationToken);
        if (target is not PrivateAccessDeploymentTargetReady)
            return new PrivateAccessDeploymentUnavailable();
        var endpoint = await endpointCatalog.ResolveAsync(
            accessSession.TenantId, resourceType, accessSession.ResourceId,
            accessSession.EndpointId, transactionSession, cancellationToken);
        if (endpoint is null)
            return new PrivateAccessEndpointUnavailable();
        if (PrivateAccessResolvedBinding.From(placement, endpoint) !=
            PrivateAccessResolvedBinding.From(accessSession))
            return new PrivateAccessBindingChanged();

        var authorizedAt = timeProvider.GetUtcNow().UtcDateTime;
        if (grant.ExpiresAtUtc <= authorizedAt)
            return new PrivateAccessGrantUnavailable();
        var expiresAt = grant.ExpiresAtUtc < authorizedAt.AddSeconds(10)
            ? grant.ExpiresAtUtc
            : authorizedAt.AddSeconds(10);
        return new PrivateAccessLeaseSnapshot(
            accessSession.TenantId,
            accessSession.SubjectUserId,
            accessSession.GrantId,
            accessSession.Id,
            accessSession.ResourceType,
            accessSession.ResourceId,
            accessSession.ResourceDeploymentId,
            accessSession.EndpointId,
            accessSession.DeploymentTargetId,
            accessSession.RuntimeCellId,
            accessSession.IsolationScopeId,
            accessSession.KubernetesNamespace,
            accessSession.ServiceName,
            NetworkPort.Create(accessSession.ServicePort),
            NetworkPort.Create(accessSession.TargetPort),
            accessSession.DeploymentRevision,
            expiresAt,
            grant.ExpiresAtUtc);
    }
}
