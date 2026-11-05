using System.Net;
using Pole.Tests.Integration.Http;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

public sealed class DeploymentTargetNetworkPolicyApiTests(
    MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task AttestationRequiresPlatformAdmin_AndUnknownTargetIsNotEligible()
    {
        var target = DeploymentTargetNetworkPolicyTests.Target();
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        await Fixture.DbContext.DeploymentTargetSettings.InsertOneAsync(
            DeploymentTargetSettings.Create(target.Id, target.CreatedAtUtc));
        var body = new UpdateDeploymentTargetNetworkPolicyBody(
            NetworkPolicyEnforcement.Supported,
            false,
            target.Revision);
        var response = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{target.Id}/network-policy",
            body);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(await Fixture.DbContext.DeploymentTargets
            .Find(DeploymentTargetFilters.AcceptingNewWorkloads())
            .ToListAsync());

        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        response = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{target.Id}/network-policy",
            body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<DeploymentTargetDto>())!;
        Assert.Equal(NetworkPolicyReadiness.EnforcementAttested, updated.NetworkPolicy.Readiness);
        Assert.True(updated.IsDefault);
        var attestationJson = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"enforcement\":\"Supported\"", attestationJson);
        Assert.Contains("\"readiness\":\"EnforcementAttested\"", attestationJson);
        Assert.Equal(target.Revision + 1, updated.Revision);
        Assert.Equal(target.WorkloadConfiguration.Revision + 1, updated.WorkloadConfiguration.Revision);
        Assert.Single(await Fixture.DbContext.DeploymentTargets
            .Find(DeploymentTargetFilters.AcceptingNewWorkloads())
            .ToListAsync());
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await Client.PutAsJsonAsync(
                $"/api/v1/platform/deployment-targets/{target.Id}/network-policy",
                body)).StatusCode);

        response = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{target.Id}/network-policy",
            body with
            {
                Enforcement = NetworkPolicyEnforcement.Unsupported,
                Revision = updated.Revision
            });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await Fixture.DbContext.DeploymentTargets
            .Find(DeploymentTargetFilters.AcceptingNewWorkloads())
            .ToListAsync());
    }

    [Fact]
    public async Task MongoAdmissionFence_UsesSerializedEnumAndCurrentConnection_IndependentlyPerTarget()
    {
        var supported = DeploymentTargetNetworkPolicyTests.Attest(
            DeploymentTargetNetworkPolicyTests.Target(),
            NetworkPolicyEnforcement.Supported,
            false);
        var unsupported = DeploymentTargetNetworkPolicyTests.Attest(
            DeploymentTargetNetworkPolicyTests.Target(),
            NetworkPolicyEnforcement.Unsupported,
            false);
        var overridden = DeploymentTargetNetworkPolicyTests.Attest(
            DeploymentTargetNetworkPolicyTests.Target(),
            NetworkPolicyEnforcement.Unknown,
            true);
        var unknown = DeploymentTargetNetworkPolicyTests.Target();
        await Fixture.DbContext.DeploymentTargets.InsertManyAsync(
            [supported, unsupported, overridden, unknown]);
        var eligible = await Fixture.DbContext.DeploymentTargets
            .Find(DeploymentTargetFilters.AcceptingNewWorkloads())
            .ToListAsync();
        Assert.Equal(new[] { supported.Id, overridden.Id }.Order(), eligible.Select(item => item.Id).Order());
        await Fixture.DbContext.DeploymentTargets.UpdateOneAsync(
            item => item.Id == supported.Id,
            Builders<DeploymentTarget>.Update.Set(
                item => item.Connection,
                DeploymentTargetConnectionFactory.CreateInCluster(2)));
        eligible = await Fixture.DbContext.DeploymentTargets
            .Find(DeploymentTargetFilters.AcceptingNewWorkloads())
            .ToListAsync();
        Assert.Equal(overridden.Id, Assert.Single(eligible).Id);

        var response = await Client.GetAsync(
            "/api/v1/deployment-targets/available?resourceType=pole/web-service&requiresPublicIngress=false");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = Assert.Single((await response.Content.ReadFromJsonAsync<AvailableDeploymentTargetDto[]>())!);
        Assert.Equal(overridden.Id, dto.Id);
        Assert.Equal(NetworkPolicyReadiness.IsolationNotEnforced, dto.NetworkPolicy.Readiness);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(overridden.ClusterIdentity, json);
        Assert.DoesNotContain("namespace", json, StringComparison.OrdinalIgnoreCase);
        Client.DefaultRequestHeaders.Remove("X-Pole-Tenant-ID");
        Assert.NotEqual(
            HttpStatusCode.OK,
            (await Client.GetAsync(
                "/api/v1/deployment-targets/available?resourceType=pole/web-service&requiresPublicIngress=false")).StatusCode);
    }
}
