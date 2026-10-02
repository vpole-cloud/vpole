using System.Net;
using System.Net.Http.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.PrivateAccess.Application.Endpoints;
using Pole.Features.PrivateAccess.Contracts;
using Pole.Features.PrivateAccess.Domain;
using Pole.Features.RedisServices.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Tests.Integration.Infrastructure;
using Pole.Tests.Integration.ResourceDeployments;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.PrivateAccess;

public sealed class PrivateAccessGatewayStatusApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    protected override bool PrivateAccessEnabled => true;

    [Fact]
    public async Task Status_RequiresPlatformAdmin_AndUsesRouteTenantWithoutActiveTenant()
    {
        var path = Path(TestData.TenantId, TestData.DefaultDeploymentTargetId);
        var unrelatedTenant = ObjectId.GenerateNewId().ToString();
        var unrelatedGrant = PrivateAccessGrantFactory.Create(
            unrelatedTenant, TestData.UserId, "pole/redis",
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(), "redis", TestData.UserId,
            DateTime.UtcNow, DateTime.UtcNow.AddHours(1)) with
        {
            ResourceType = "invalid-resource-type"
        };
        await Fixture.Database.GetCollection<PrivateAccessGrant>("PrivateAccessGrants")
            .InsertOneAsync(unrelatedGrant);
        using var forbidden = await Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        using var allowed = await Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var status = await allowed.Content.ReadFromJsonAsync<PrivateAccessGatewayStatusDto>();
        Assert.Equal(PrivateAccessGatewayAvailability.NotRequired,
            Assert.IsType<PrivateAccessGatewayStatusDto>(status).Availability);

        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, "not-a-tenant");
        using var ignoredHeader = await Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, ignoredHeader.StatusCode);
        using var invalid = await Client.GetAsync(
            Path("bad-tenant", TestData.DefaultDeploymentTargetId));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var missingTarget = await Client.GetAsync(
            Path(TestData.TenantId, ObjectId.GenerateNewId().ToString()));
        Assert.Equal(HttpStatusCode.NotFound, missingTarget.StatusCode);
    }

    [Fact]
    public async Task Status_FollowsDesiredRegistrationAndCurrentBrokerLease_WithoutExposingCredentials()
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var resource = RedisFactory.Create(TestData.TenantId, "orders",
            ObjectId.GenerateNewId().ToString(),
            new RedisSpec(new Storage(1, TestData.DefaultStorageProfileId)),
            RedisFactory.DefaultCreateOptions);
        await Fixture.Database.GetCollection<Redis>("RedisServices").InsertOneAsync(resource);
        var deploymentId = ObjectId.GenerateNewId().ToString();
        await Fixture.DbContext.ResourceDeployments.InsertOneAsync(
            AppliedDeployment(resource.Id, deploymentId));
        var grant = PrivateAccessGrantFactory.Create(
            TestData.TenantId, TestData.UserId, "pole/redis", resource.Id,
            deploymentId, "redis", TestData.UserId,
            DateTime.UtcNow, DateTime.UtcNow.AddHours(1));
        await Fixture.Database.GetCollection<PrivateAccessGrant>("PrivateAccessGrants")
            .InsertOneAsync(grant);
        var path = Path(TestData.TenantId, TestData.DefaultDeploymentTargetId);

        var provisioning = await GetStatusAsync(path);
        Assert.True(provisioning.Required);
        Assert.Equal(PrivateAccessGatewayAvailability.Provisioning, provisioning.Availability);

        var gatewayId = PrivateAccessGatewayIds.StableId(
            TestData.TenantId, TestData.DefaultDeploymentTargetId);
        var pin = new string('a', 64);
        var gateway = PrivateAccessGatewayFactory.Create(
            gatewayId, TestData.TenantId, TestData.DefaultDeploymentTargetId,
            pin, DateTime.UtcNow.AddDays(1), []);
        var gateways = Fixture.Database.GetCollection<PrivateAccessGateway>("PrivateAccessGateways");
        await gateways.InsertOneAsync(gateway);
        Assert.Equal(PrivateAccessGatewayAvailability.AwaitingConnection,
            (await GetStatusAsync(path)).Availability);

        var contacted = gateway with
        {
            LastContactAtUtc = DateTime.UtcNow,
            Version = PrivateAccessGatewayProtocolMetadata.Version
        };
        await gateways.ReplaceOneAsync(item => item.Id == gatewayId, contacted);
        var owner = PrivateAccessBrokerOwnerFactory.Create(
            gatewayId, TestData.TenantId, TestData.DefaultDeploymentTargetId, 1, 1) with
        {
            Epoch = 1,
            InstanceId = "server-1",
            InternalOrigin = "https://server-1.internal:9445",
            LeaseExpiresAtUtc = DateTime.UtcNow.AddSeconds(30)
        };
        var owners = Fixture.Database.GetCollection<PrivateAccessBrokerOwner>("PrivateAccessBrokerOwners");
        await owners.InsertOneAsync(owner);
        var connected = await GetStatusAsync(path);
        Assert.Equal(PrivateAccessGatewayAvailability.Connected, connected.Availability);
        Assert.Equal(PrivateAccessGatewayProtocolMetadata.Version, connected.ProtocolVersion);
        Assert.Equal([PrivateAccessGatewayProtocolMetadata.TcpForwardCapability],
            connected.Capabilities);
        Assert.Equal(1, connected.CredentialGeneration);
        Assert.NotNull(connected.LastContactAtUtc);

        using var response = await Client.GetAsync(path);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(pin, json, StringComparison.Ordinal);
        Assert.DoesNotContain(owner.InternalOrigin!, json, StringComparison.Ordinal);

        await owners.ReplaceOneAsync(item => item.Id == gatewayId,
            owner with { LeaseExpiresAtUtc = DateTime.UnixEpoch });
        Assert.Equal(PrivateAccessGatewayAvailability.Disconnected,
            (await GetStatusAsync(path)).Availability);

        await owners.ReplaceOneAsync(item => item.Id == gatewayId,
            owner with { GatewayGeneration = 2 });
        Assert.Equal(PrivateAccessGatewayAvailability.Disconnected,
            (await GetStatusAsync(path)).Availability);

        await gateways.ReplaceOneAsync(item => item.Id == gatewayId,
            contacted with { CertificateNotAfterUtc = DateTime.UnixEpoch });
        Assert.Equal(PrivateAccessGatewayAvailability.CredentialExpired,
            (await GetStatusAsync(path)).Availability);

        SetPrivateAccessTargetState(false, false, NetworkPolicyReadiness.EnforcementAttested);
        Assert.Equal(PrivateAccessGatewayAvailability.TargetDisabled,
            (await GetStatusAsync(path)).Availability);
        SetPrivateAccessTargetState(true, true, NetworkPolicyReadiness.EnforcementAttested);
        Assert.Equal(PrivateAccessGatewayAvailability.TargetDeletionRequested,
            (await GetStatusAsync(path)).Availability);
        SetPrivateAccessTargetState(true, false, NetworkPolicyReadiness.EnforcementRequired);
        Assert.Equal(PrivateAccessGatewayAvailability.NetworkPolicyUnavailable,
            (await GetStatusAsync(path)).Availability);
        SetPrivateAccessTargetState(true, false, NetworkPolicyReadiness.EnforcementAttested);

        await Fixture.Database.GetCollection<PrivateAccessGrant>("PrivateAccessGrants")
            .ReplaceOneAsync(item => item.Id == grant.Id,
                grant.Revoke(TestData.UserId, DateTime.UtcNow));
        var removing = await GetStatusAsync(path);
        Assert.False(removing.Required);
        Assert.Equal(PrivateAccessGatewayAvailability.Removing, removing.Availability);

        var otherTenant = ObjectId.GenerateNewId().ToString();
        var isolated = await GetStatusAsync(Path(otherTenant, TestData.DefaultDeploymentTargetId));
        Assert.False(isolated.Required);
        Assert.Equal(PrivateAccessGatewayAvailability.NotRequired, isolated.Availability);
        Assert.Null(isolated.LastContactAtUtc);
    }

    private async Task<PrivateAccessGatewayStatusDto> GetStatusAsync(string path)
    {
        using var response = await Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<PrivateAccessGatewayStatusDto>(
            await response.Content.ReadFromJsonAsync<PrivateAccessGatewayStatusDto>());
    }

    private static string Path(string tenantId, string targetId) =>
        $"/api/v1/platform/private-access/gateways/{tenantId}/{targetId}";

    private static ResourceDeployment AppliedDeployment(string resourceId, string deploymentId)
    {
        var now = DateTime.UtcNow;
        var deployment = ResourceDeploymentFactory.Create(
            TestData.TenantId,
            ResourceTypeId.Create("pole/redis"),
            resourceId,
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            ResolvedResourcePlacement.Create(
                ObjectId.GenerateNewId().ToString(),
                TestData.DefaultDeploymentTargetId,
                "pole-private-access-test",
                runtimeCellGeneration: 1,
                targetWorkloadConfigurationRevision: 1,
                ingress: null,
                storage: null,
                ResourceDeploymentTestData.Compute(),
                now),
            ResourcePlacementCapability.SingleTarget,
            runtimeCellReady: true,
            new ResourceDeploymentFactory.CreateOptions(deploymentId, now));
        return deployment with
        {
            State = new AppliedResourceDeploymentState(1, now)
        };
    }
}

public sealed class PrivateAccessGatewayDisabledApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Status_ReportsFeatureDisabled()
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        using var response = await Client.GetAsync(
            $"/api/v1/platform/private-access/gateways/{TestData.TenantId}/{TestData.DefaultDeploymentTargetId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var status = await response.Content.ReadFromJsonAsync<PrivateAccessGatewayStatusDto>();
        Assert.Equal(PrivateAccessGatewayAvailability.FeatureDisabled,
            Assert.IsType<PrivateAccessGatewayStatusDto>(status).Availability);
    }
}
