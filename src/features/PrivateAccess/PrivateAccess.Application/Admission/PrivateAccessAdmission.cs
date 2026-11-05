using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.PrivateAccess.Application.Catalog;
using Pole.Features.PrivateAccess.Application.Gateway;
using Pole.Features.PrivateAccess.Application.Grants;
using Pole.Features.PrivateAccess.Application.Mongo;
using Pole.Features.PrivateAccess.Application.Sessions;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Tenants.Contracts;
using Pole.Workloads;

namespace Pole.Features.PrivateAccess.Application.Admission;

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
            PrivateAccessFilters.Grant(accessSession.TenantId, accessSession.Grant.Id),
            Builders<PrivateAccessGrant>.Update.Inc(item => item.ReferenceFence, 1),
            new FindOneAndUpdateOptions<PrivateAccessGrant> { ReturnDocument = ReturnDocument.After },
            cancellationToken);
        if (grant is null || !grant.IsActive(now))
            return new PrivateAccessGrantUnavailable();
        if (PrivateAccessGrantBinding.From(grant) !=
            PrivateAccessGrantBinding.From(accessSession))
            return new PrivateAccessBindingChanged();

        var member = await loadTenantMember(
            new LoadTenantMemberRequest(
                TenantId: accessSession.TenantId,
                UserId: accessSession.SubjectUserId),
            session: transactionSession,
            cancellationToken: cancellationToken);
        if (member is not TenantMemberSnapshot)
            return new PrivateAccessSubjectMembershipRequired();

        var resourceType = ResourceTypeId.Create(accessSession.Grant.ResourceType);
        var registered = await fenceResource(
            new FenceRegisteredResourceRequest(
                TenantId: accessSession.TenantId,
                ResourceTypeId: resourceType,
                ResourceId: accessSession.Grant.ResourceId),
            session: transactionSession,
            cancellationToken: cancellationToken);
        if (registered is null)
            return new PrivateAccessDeploymentUnavailable();
        var placement = await loadPlacement(
            new LoadExactResourceDeploymentAccessPlacementRequest(
                TenantId: accessSession.TenantId,
                ResourceType: resourceType,
                ResourceId: accessSession.Grant.ResourceId,
                DeploymentId: accessSession.Grant.ResourceDeploymentId),
            session: transactionSession,
            cancellationToken: cancellationToken);
        if (placement is null ||
            placement.ResourceGroupId != registered.ResourceGroupId ||
            placement.Status != ResourceDeploymentStatus.Applied ||
            placement.DesiredOperation != ResourceDeploymentDesiredOperation.Apply)
            return new PrivateAccessDeploymentUnavailable();
        var target = await fenceTarget(
            placement.DeploymentTargetId, transactionSession, cancellationToken);
        if (target is not PrivateAccessDeploymentTargetReady)
            return new PrivateAccessDeploymentUnavailable();
        var endpoint = await endpointCatalog.ResolveAsync(
            accessSession.TenantId, resourceType, accessSession.Grant.ResourceId,
            accessSession.Grant.EndpointId, transactionSession, cancellationToken);
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
            accessSession.Grant.Id,
            accessSession.Id,
            accessSession.Grant.ResourceType,
            accessSession.Grant.ResourceId,
            accessSession.Grant.ResourceDeploymentId,
            accessSession.Grant.EndpointId,
            accessSession.Route.DeploymentTargetId,
            accessSession.Route.RuntimeCellId,
            accessSession.Route.IsolationScopeId,
            accessSession.Route.KubernetesNamespace,
            accessSession.Route.ServiceName,
            NetworkPort.Create(accessSession.Route.ServicePort),
            NetworkPort.Create(accessSession.Route.TargetPort),
            accessSession.Route.DeploymentRevision,
            expiresAt,
            grant.ExpiresAtUtc);
    }
}
