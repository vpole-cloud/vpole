using Pole.Features.Domains.Application.Endpoints;
using Pole.Features.Domains.Application.Persistence;
using Pole.Features.Domains.Contracts;
using Xunit;

namespace Pole.Tests.Integration.Domains;

public sealed class DomainHandlerOutcomeTests
{
    [Fact]
    public async Task List_wraps_tenant_scoped_snapshots_in_its_request_outcome()
    {
        const string tenantId = "507f1f77bcf86cd799439011";
        const string deploymentTargetId = "507f1f77bcf86cd799439012";
        string? receivedTenantId = null;
        string? receivedDeploymentTargetId = null;
        LoadTenantDomainZones loadZones = (requestedTenantId, requestedDeploymentTargetId, _) =>
        {
            receivedTenantId = requestedTenantId;
            receivedDeploymentTargetId = requestedDeploymentTargetId;
            return Task.FromResult<IReadOnlyList<TenantDomainZoneSnapshot>>([]);
        };
        var handler = new GetDomainZonesHandler(loadZones);

        var outcome = await handler.HandleAsync(new GetDomainZonesRequest(
            tenantId,
            deploymentTargetId));

        var zones = outcome switch
        {
            IReadOnlyList<TenantDomainZoneSnapshot> value => value,
            _ => throw new Xunit.Sdk.XunitException(
                "Expected the domain-zone list outcome.")
        };
        Assert.Empty(zones);
        Assert.Equal(tenantId, receivedTenantId);
        Assert.Equal(deploymentTargetId, receivedDeploymentTargetId);
    }
}
