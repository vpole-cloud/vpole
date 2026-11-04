using Microsoft.Extensions.Options;
using Pole.Features.PrivateAccess.Contracts;

namespace Pole.Features.PrivateAccess.Runtime;

public sealed class PrivateAccessBrokerRouting(
    IOptions<PrivateAccessBrokerOptions> configured,
    ClaimPrivateAccessBrokerOwner claim,
    RenewPrivateAccessBrokerOwner renew,
    ReleasePrivateAccessBrokerOwner release,
    LoadPrivateAccessBrokerOwner load)
{
    public bool IsLocal(PrivateAccessBrokerOwnerLease owner) =>
        owner.InstanceId == configured.Value.InstanceId &&
        owner.InternalOrigin == configured.Value.InternalOrigin;

    public Task<PrivateAccessBrokerOwnerLease?> FindAsync(
        string tenantId,
        string targetId,
        CancellationToken cancellationToken) =>
        load(tenantId, targetId, cancellationToken);

    public async Task<PrivateAccessBrokerOwnerLease?> ClaimOrFindAsync(
        PrivateAccessGatewayIdentity gateway,
        CancellationToken cancellationToken)
    {
        var current = await FindAsync(gateway.TenantId,
            gateway.DeploymentTargetId, cancellationToken);
        if (current is not null && !IsLocal(current))
            return current;
        var result = await claim(
            new ClaimPrivateAccessBrokerOwnerRequest(
                Gateway: gateway,
                InstanceId: configured.Value.InstanceId,
                InternalOrigin: configured.Value.InternalOrigin),
            cancellationToken: cancellationToken);
        return result switch
        {
            PrivateAccessBrokerOwnerLease owned => owned,
            PrivateAccessBrokerOwnerBusy => await FindAsync(gateway.TenantId,
                gateway.DeploymentTargetId, cancellationToken),
            PrivateAccessBrokerGatewayUnavailable => null
        };
    }

    public async Task<bool> IsCurrentAsync(
        PrivateAccessBrokerOwnerLease lease,
        CancellationToken cancellationToken)
    {
        var current = await FindAsync(lease.Gateway.TenantId,
            lease.Gateway.DeploymentTargetId, cancellationToken);
        return current is not null && IsLocal(current) &&
               current.Gateway == lease.Gateway &&
               current.Epoch == lease.Epoch;
    }

    public Task<bool> RenewAsync(PrivateAccessBrokerOwnerLease lease,
        CancellationToken cancellationToken) => renew(lease, cancellationToken);

    public Task ReleaseAsync(PrivateAccessBrokerOwnerLease lease,
        CancellationToken cancellationToken) => release(lease, cancellationToken);
}
