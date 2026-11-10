using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Testing;
using Pole.Features.Tenants.Testing;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Events;
using Pole.Mediator;
using Pole.Testing;
using Pole.Workloads;
using Xunit;

namespace Pole.Features.WebServices.Tests.Integration;

[Collection(nameof(WebServicesMongoCollection))]
public sealed class CreateWebServiceTests(
    WebServicesMongoFixture fixture
) : IAsyncLifetime
{
    private ResourceGroupTestData _groups = null!;
    private CreateWebServicePlatform _platform = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        var tenant = await new TenantTestData(fixture.Database).CreateAsync(id: TestPlatformDefaults.TenantId);
        _groups = new ResourceGroupTestData(fixture.Database);
        var group = await _groups.CreateAsync(tenant);
        _platform = new CreateWebServicePlatform(group);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Create_PublicRequestsGeneratedHostnameAndMultiTargetPlacement()
    {
        var request = Request();
        var outcome = await Send(
            request with { Body = request.Body with { Exposure = WebServiceExposureMode.Public } });
        var created = Assert.IsType<CreateWebServiceResponse>(outcome.Value);
        var service = await fixture.WebServices.Find(item => item.Id == created.WebServiceId).SingleAsync();
        Assert.Equal(request.TenantId, service.TenantId);
        Assert.Equal(_platform.IsolationScopeId, service.IsolationScopeId);
        var placement = Assert.Single(created.Deployments);
        var deployment = Assert.Single(_platform.Deployments);
        Assert.Equal(ResourcePlacementCapability.MultiTarget, deployment.PlacementCapability);
        Assert.Equal(service.Id, deployment.ResourceId);
        Assert.Equal(2, placement.FixedReplicas);
        var zone = Assert.Single(_platform.Zones);
        Assert.Equal(request.TenantId, zone.TenantId);
        Assert.Equal(placement.DeploymentTargetId, zone.DeploymentTargetId);
        var binding = Assert.Single(_platform.Bindings);
        Assert.Equal(request.TenantId, binding.TenantId);
        Assert.Equal(placement.DeploymentTargetId, binding.DeploymentTargetId);
        Assert.Equal(WebServiceResourceDeployment.ResourceType, binding.ResourceType);
        Assert.Equal(service.Id, binding.ResourceId);
        Assert.Equal(placement.DeploymentId, binding.ResourceDeploymentId);
        Assert.Equal("webservice-" + service.Id, binding.RelativeName);
        Assert.Equal(binding.RelativeName + ".apps.test.local", Assert.Single(placement.Ingress!.Hostnames));
        await _groups.AssertRegisteredAsync(
            request.TenantId,
            _platform.GroupId,
            WebServiceResourceDeployment.ResourceType,
            service.Id,
            service.Name);
        var release = await fixture.Releases.Find(item => item.WebServiceId == service.Id).SingleAsync();
        Assert.Equal(WebServiceReleaseAction.Create, release.Action);
        Assert.Equal(placement.DeploymentId, Assert.Single(release.Deployments).ResourceDeploymentId);
        var message = await fixture.OutboxMessages
            .Find(item => item.EventType == "pole.web-services/v1/web-service-created")
            .SingleAsync();
        Assert.Equal(request.TenantId, message.TenantId);
        var payload = JsonSerializer.Deserialize<WebServiceCreatedEvent>(
            message.PayloadJson,
            EventExtensions.SerializerOptions);
        Assert.NotNull(payload);
        Assert.Equal(service.Id, payload.WebServiceId);
        var snapshot = Assert.Single(payload.Deployments);
        Assert.Equal(placement.DeploymentId, snapshot.ResourceDeploymentId);
        Assert.Equal(placement.Ingress.Hostnames, snapshot.Hostnames);
    }

    [Fact]
    public async Task Create_PrivateByDefault_PersistsNoHostnameOrIngress()
    {
        var outcome = await Send(Request());
        var created = Assert.IsType<CreateWebServiceResponse>(outcome.Value);
        var service = await fixture.WebServices.Find(item => item.Id == created.WebServiceId).SingleAsync();
        var deployment = Assert.Single(created.Deployments);
        Assert.Equal(WebServiceExposureMode.Private, service.Exposure.Mode);
        Assert.Equal("Private", service.ToDto([deployment]).Exposure.Mode);
        Assert.Null(deployment.Ingress);
        Assert.Empty(_platform.Zones);
        Assert.Empty(_platform.Bindings);
    }

    [Fact]
    public async Task Create_PublicWithAnyTargetMissingIngressSourceRejectsWholeRequest()
    {
        var unavailable = ObjectId.GenerateNewId().ToString();
        _platform.Targets.AddIneligiblePublicIngressTarget(unavailable, "apps.missing-source.test.local");
        var request = Request();
        var outcome = await Send(
            request with
            {
                Body = request.Body with
                {
                    Exposure = WebServiceExposureMode.Public,
                    Placement = new WebServicePlacementRequest(
                        null,
                        [
                            new WebServiceDeploymentTargetRequest(TestPlatformDefaults.DeploymentTargetId),
                            new WebServiceDeploymentTargetRequest(unavailable),
                        ]),
                },
            });
        var error = Assert.IsType<CreateWebServiceError>(outcome.Value);
        var placement = Assert.IsType<CreateWebServicePlacementError>(error.Value);
        Assert.Equal(unavailable, Assert.IsType<CreateWebServiceTargetUnavailable>(placement.Value).DeploymentTargetId);
        await AssertNoCreationAsync();
    }

    [Fact]
    public async Task Create_ReturnsNotFound_WhenResourceGroupDoesNotExist()
    {
        var missingId = ObjectId.GenerateNewId().ToString();
        var outcome = await Send(Request(missingId));
        var error = Assert.IsType<CreateWebServiceError>(outcome.Value);
        var ownership = Assert.IsType<ResourceGroupOwnershipError>(error.Value);
        Assert.Equal(missingId, Assert.IsType<ResourceGroupOwnershipNotFound>(ownership.Value).ResourceGroupId);
        await AssertNoCreationAsync();
    }

    private async Task AssertNoCreationAsync()
    {
        Assert.Empty(_platform.Deployments);
        await _groups.AssertNoRegistrationsAsync(TestPlatformDefaults.TenantId);
        Assert.Empty(_platform.Bindings);
        Assert.Empty(_platform.Zones);
        Assert.Equal(0L, await fixture.WebServices.CountDocumentsAsync(_ => true));
        Assert.Equal(0L, await fixture.Releases.CountDocumentsAsync(_ => true));
        Assert.Equal(0L, await fixture.OutboxMessages.CountDocumentsAsync(_ => true));
    }

    private CreateWebServiceRequest Request(string? groupId = null) =>
        WebServiceTestData.Create(groupId ?? _platform.GroupId);

    private async Task<CreateWebServiceOutcome> Send(CreateWebServiceRequest request)
    {
        using var provider = WebServicesTestHost.Create(fixture, _platform);
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
        return Assert.IsType<CreateWebServiceOutcome>(result.Value);
    }
}
