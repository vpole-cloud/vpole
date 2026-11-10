using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.ResourceGroups.Testing;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Configuration;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Endpoints.Releases;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.Testing;
using Xunit;

namespace Pole.Features.WebServices.Tests.Integration;

[Collection(nameof(WebServicesMongoCollection))]
public sealed class WebServiceMutationTests(
    WebServicesMongoFixture fixture
) : IAsyncLifetime
{
    private TestResourceGroup _group = null!;
    private CreateWebServicePlatform _platform = null!;
    private IServiceScope _scope = null!;
    private ServiceProvider _services = null!;
    private IMediator Mediator => _scope.ServiceProvider.GetRequiredService<IMediator>();

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        _group = await new ResourceGroupTestData(fixture.Database).CreateAsync(TestPlatformDefaults.TenantId);
        _platform = new CreateWebServicePlatform(_group);
        _services = WebServicesTestHost.Create(fixture, _platform);
        _scope = _services.CreateScope();
    }

    public Task DisposeAsync()
    {
        _scope.Dispose();
        _services.Dispose();
        return Task.CompletedTask;
    }

    [Theory]
    [InlineData("registry.test", "tenant/application", "built", true)]
    [InlineData("registry.test", "tenant/application", "next", true)]
    [InlineData("another.test", "tenant/application", "built", false)]
    [InlineData("registry.test", "tenant/another", "built", false)]
    public async Task SharedUpdate_preserves_provider_owned_pull_reference_only_for_the_same_repository(
        string server,
        string name,
        string tag,
        bool retainPullReference)
    {
        var group = _group;
        var created = (await Mediator.Send(CreateRequest(group.Id))).AssertSuccess();
        var services = fixture.WebServices;
        var current = await services.Find(item => item.Id == created.WebServiceId).SingleAsync();
        current = current with
        {
            Spec = current.Spec with
            {
                Image = new ImageInfo("registry.test", "tenant/application", "built", "managed-pull"),
            },
        };
        await services.ReplaceOneAsync(item => item.Id == current.Id, current);
        var dto = current.Spec.ToDto() with
        {
            Image = new ImageInfoDto(server, name, tag),
            Config = current.Spec.Config.ToDto() with
            {
                EnvironmentVariables = current.Spec.Config.EnvironmentVariables.SetItem("APP_MODE", "production"),
            },
        };

        var updated = (await Mediator.Send(
            new UpdateWebServiceRequest(
                current.Id,
                new UpdateWebServiceRequestBody(dto, current.Tags, current.Exposure.Mode, current.ActiveRevision),
                current.TenantId))).AssertSuccess();

        var saved = await services.Find(item => item.Id == current.Id).SingleAsync();
        var release = await fixture.Releases
            .Find(item => item.WebServiceId == current.Id && item.ResourceRevision == updated.ResourceRevision)
            .SingleAsync();
        var expectedPullReference = retainPullReference ? "managed-pull" : null;
        Assert.Equal(expectedPullReference, saved.Spec.Image.ResolvedImage!.SecretName);
        Assert.Equal(expectedPullReference, release.Spec.Image.ResolvedImage!.SecretName);
        Assert.Equal(tag, saved.Spec.Image.ResolvedImage!.Tag);
        Assert.Equal("production", saved.Spec.Config.EnvironmentVariables["APP_MODE"]);
        Assert.DoesNotContain("managed-pull", JsonSerializer.Serialize(saved.Spec.ToDto()));
    }

    [Fact]
    public async Task Update_with_stale_expected_revision_rejects_without_deployment_or_outbox_changes()
    {
        var group = _group;
        var created = (await Mediator.Send(CreateRequest(group.Id))).AssertSuccess();
        var current = await fixture.WebServices.Find(item => item.Id == created.WebServiceId).SingleAsync();
        var result = await Mediator.Send(
            new UpdateWebServiceRequest(
                current.Id,
                new UpdateWebServiceRequestBody(
                    current.Spec.ToDto(),
                    current.Tags,
                    current.Exposure.Mode,
                    ExpectedRevision: 99),
                current.TenantId));
        var error = Assert.IsType<UpdateWebServiceError>(result.AssertHandlerOutcome().Value);
        Assert.IsType<WebServiceUpdateConflict>(error.Value);
        Assert.Equal(
            current.ActiveRevision,
            (await fixture.WebServices.Find(item => item.Id == current.Id).SingleAsync()).ActiveRevision);
        Assert.Equal(0, await CountEventsAsync("pole.web-services/v1/web-service-updated"));
    }

    [Fact]
    public async Task Rollback_RestoresSucceededReleaseSpec()
    {
        var group = _group;
        var template = CreateRequest(group.Id);
        var created = (await Mediator.Send(
            template with
            {
                Body = template.Body with { Exposure = WebServiceExposureMode.Private },
            })).AssertSuccess();
        await fixture.Releases.UpdateOneAsync(
            release => release.WebServiceId == created.WebServiceId && release.ResourceRevision == 1,
            Builders<WebServiceRelease>.Update
                .Set(release => release.Outcome, WebServiceReleaseOutcome.Succeeded)
                .Set(release => release.CompletedAtUtc, DateTime.UtcNow));
        var updatedSpec = WebServiceTestData.Spec() with
        {
            Image = new ImageInfoDto("docker.io", "nginx", "1.28"),
        };
        (await Mediator.Send(
            new UpdateWebServiceRequest(
                created.WebServiceId,
                new UpdateWebServiceRequestBody(updatedSpec, [], WebServiceExposureMode.Public),
                TestPlatformDefaults.TenantId))).AssertSuccess();

        var rollback = (await Mediator.Send(
            new RollbackWebServiceRequest(
                created.WebServiceId,
                new RollbackWebServiceRequestBody(1, 2),
                TestPlatformDefaults.TenantId))).AssertSuccess();

        Assert.Equal(3, rollback.ResourceRevision);
        var saved = await fixture.WebServices.Find(service => service.Id == created.WebServiceId).SingleAsync();
        Assert.Equal("1.27", saved.Spec.Image.ResolvedImage!.Tag);
        Assert.Equal(WebServiceExposureMode.Public, saved.Exposure.Mode);
        var release = await fixture.Releases.Find(item => item.Id == rollback.ReleaseId).SingleAsync();
        Assert.Equal(WebServiceReleaseAction.Rollback, release.Action);
        Assert.Equal("1.27", release.Spec.Image.ResolvedImage!.Tag);
    }

    [Fact]
    public async Task Rollback_PublicRejectsHistoricalUdpSpecWithoutMutation()
    {
        var group = _group;
        var template = CreateRequest(group.Id);
        var privateSpec = WebServiceTestData.Spec() with
        {
            Config = WebServiceTestData.Spec().Config with
            {
                PortMappings = [new PortMappingDto(80, "http", "UDP", "datagram")],
            },
        };
        var created = (await Mediator.Send(
            template with
            {
                Body = template.Body with
                {
                    Exposure = WebServiceExposureMode.Private,
                    Spec = privateSpec,
                },
            })).AssertSuccess();
        await fixture.Releases.UpdateOneAsync(
            release => release.WebServiceId == created.WebServiceId && release.ResourceRevision == 1,
            Builders<WebServiceRelease>.Update
                .Set(release => release.Outcome, WebServiceReleaseOutcome.Succeeded)
                .Set(release => release.CompletedAtUtc, DateTime.UtcNow));
        (await Mediator.Send(
            new UpdateWebServiceRequest(
                created.WebServiceId,
                new UpdateWebServiceRequestBody(WebServiceTestData.Spec(), [], WebServiceExposureMode.Public),
                TestPlatformDefaults.TenantId))).AssertSuccess();

        var beforeService = await fixture.WebServices.Find(service => service.Id == created.WebServiceId).SingleAsync();
        var beforeApplyCount = _platform.ApplyRequests.Count;
        var beforeReleaseCount
            = await fixture.Releases.CountDocumentsAsync(release => release.WebServiceId == created.WebServiceId);
        var beforeUpdatedEvents = await CountEventsAsync("pole.web-services/v1/web-service-updated");

        var result = await Mediator.Send(
            new RollbackWebServiceRequest(
                created.WebServiceId,
                new RollbackWebServiceRequestBody(1, 2),
                TestPlatformDefaults.TenantId));

        var incompatible = Assert.IsType<RollbackWebServicePublicSpecIncompatible>(
            Assert.IsType<RollbackWebServiceOutcome>(result.Value).Value);
        Assert.Equal(created.WebServiceId, incompatible.WebServiceId);
        Assert.Equal(1, incompatible.TargetRevision);
        var afterService = await fixture.WebServices.Find(service => service.Id == created.WebServiceId).SingleAsync();
        Assert.Equal(beforeService.ActiveRevision, afterService.ActiveRevision);
        Assert.Equal(beforeService.Revision, afterService.Revision);
        Assert.Equal(beforeApplyCount, _platform.ApplyRequests.Count);
        Assert.Equal(
            beforeReleaseCount,
            await fixture.Releases.CountDocumentsAsync(release => release.WebServiceId == created.WebServiceId));
        Assert.Equal(beforeUpdatedEvents, await CountEventsAsync("pole.web-services/v1/web-service-updated"));
    }

    [Fact]
    public async Task Rollback_PrivateAllowsHistoricalUdpSpec()
    {
        var group = _group;
        var template = CreateRequest(group.Id);
        var privateSpec = WebServiceTestData.Spec() with
        {
            Config = WebServiceTestData.Spec().Config with
            {
                PortMappings = [new PortMappingDto(80, "http", "UDP", "datagram")],
            },
        };
        var created = (await Mediator.Send(
            template with
            {
                Body = template.Body with
                {
                    Exposure = WebServiceExposureMode.Private,
                    Spec = privateSpec,
                },
            })).AssertSuccess();
        await fixture.Releases.UpdateOneAsync(
            release => release.WebServiceId == created.WebServiceId && release.ResourceRevision == 1,
            Builders<WebServiceRelease>.Update
                .Set(release => release.Outcome, WebServiceReleaseOutcome.Succeeded)
                .Set(release => release.CompletedAtUtc, DateTime.UtcNow));
        (await Mediator.Send(
            new UpdateWebServiceRequest(
                created.WebServiceId,
                new UpdateWebServiceRequestBody(WebServiceTestData.Spec(), [], WebServiceExposureMode.Private),
                TestPlatformDefaults.TenantId))).AssertSuccess();

        var rollback = (await Mediator.Send(
            new RollbackWebServiceRequest(
                created.WebServiceId,
                new RollbackWebServiceRequestBody(1, 2),
                TestPlatformDefaults.TenantId))).AssertSuccess();

        var service = await fixture.WebServices.Find(item => item.Id == created.WebServiceId).SingleAsync();
        Assert.Equal(WebServiceExposureMode.Private, service.Exposure.Mode);
        Assert.Equal("UDP", service.Spec.Config.PortMappings.Single().Protocol);
        Assert.Equal(3, rollback.ResourceRevision);
    }

    [Fact]
    public async Task Rollback_DoesNotReadSucceededReleaseFromAnotherTenant()
    {
        const string otherTenantId = "507f1f77bcf86cd799439022";
        var group = _group;
        var created = (await Mediator.Send(CreateRequest(group.Id))).AssertSuccess();
        var placement = Assert.Single(created.Deployments);
        var foreign = WebServiceReleaseFactory.CreatePending(
                otherTenantId,
                created.WebServiceId,
                7,
                WebServiceReleaseAction.Update,
                null,
                null,
                WebServiceTestData.Spec().ToDomain(),
                [
                    new WebServiceReleaseDeployment(
                        placement.DeploymentId,
                        placement.DeploymentTargetId,
                        7,
                        placement.DesiredGeneration,
                        WebServiceReleaseDeploymentOperation.Apply,
                        WebServiceReleaseDeploymentOutcome.Succeeded,
                        DateTime.UtcNow,
                        null,
                        null),
                ],
                null,
                null,
                new WebServiceReleaseFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow),
                WebServiceExposure.Create(WebServiceExposureMode.Public)) with
            {
                Outcome = WebServiceReleaseOutcome.Succeeded,
                CompletedAtUtc = DateTime.UtcNow,
            };
        await fixture.Releases.InsertOneAsync(foreign);

        var rollback = await Mediator.Send(
            new RollbackWebServiceRequest(
                created.WebServiceId,
                new RollbackWebServiceRequestBody(7, 1),
                TestPlatformDefaults.TenantId));
        var rollbackError = rollback.AssertFailure();
        var rollbackState = Assert.IsType<RollbackWebServiceStateError>(rollbackError.Value);
        Assert.IsType<WebServiceReleaseNotFound>(rollbackState.Value);
        _scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current = new TenantContext(
            otherTenantId,
            "Other tenant",
            TenantRole.Member,
            false);
        var history = await Mediator.Send(
            new GetWebServiceReleasesRequest
            {
                Id = created.WebServiceId,
                TenantId = otherTenantId,
            });
        Assert.IsType<WebServiceNotFound>(history.AssertFailure());
    }

    [Fact]
    public async Task Equivalent_HTTP_configuration_is_a_no_op_without_releases_or_outbox_writes()
    {
        var created = (await Mediator.Send(WebServiceTestData.Create(_group.Id))).AssertSuccess();
        var service = await fixture.WebServices.Find(item => item.Id == created.WebServiceId).SingleAsync();
        var before = await fixture.OutboxMessages.CountDocumentsAsync(_ => true);
        var accepted = (await Mediator.Send(
            new UpdateWebServiceRequest(
                service.Id,
                new UpdateWebServiceRequestBody(
                    service.Spec.ToDto(),
                    service.Tags,
                    service.Exposure.Mode,
                    service.ActiveRevision),
                service.TenantId))).AssertSuccess();
        Assert.Equal(service.ActiveRevision, accepted.ResourceRevision);
        var stored = await fixture.WebServices.Find(item => item.Id == service.Id).SingleAsync();
        Assert.Equal(service.Revision, stored.Revision);
        Assert.Empty(_platform.ApplyRequests);
        Assert.Equal(before, await fixture.OutboxMessages.CountDocumentsAsync(_ => true));
        Assert.Equal(1L, await fixture.Releases.CountDocumentsAsync(item => item.WebServiceId == service.Id));
    }

    [Fact]
    public async Task Changed_configuration_still_requests_a_new_physical_generation()
    {
        var created = (await Mediator.Send(WebServiceTestData.Create(_group.Id))).AssertSuccess();
        var service = await fixture.WebServices.Find(item => item.Id == created.WebServiceId).SingleAsync();
        var placement = Assert.Single(created.Deployments);
        var spec = service.Spec.ToDto();
        spec = spec with
        {
            Config = spec.Config with
            {
                EnvironmentVariables = spec.Config.EnvironmentVariables.SetItem("MODE", "changed"),
            },
        };
        var accepted = (await Mediator.Send(
            new UpdateWebServiceRequest(
                service.Id,
                new UpdateWebServiceRequestBody(spec, service.Tags, service.Exposure.Mode, service.ActiveRevision),
                service.TenantId))).AssertSuccess();
        Assert.Equal(service.ActiveRevision + 1, accepted.ResourceRevision);
        Assert.Equal(placement.DesiredGeneration + 1, Assert.Single(accepted.Deployments).DesiredGeneration);
        Assert.Equal(accepted.ResourceRevision, Assert.Single(_platform.ApplyRequests).ResourceRevision);
        Assert.Equal(1L, await CountEventsAsync("pole.web-services/v1/web-service-updated"));
    }

    private static CreateWebServiceRequest CreateRequest(string groupId)
    {
        var request = WebServiceTestData.Create(groupId);
        return request with { Body = request.Body with { Exposure = WebServiceExposureMode.Public } };
    }

    private Task<long> CountEventsAsync(string eventType) =>
        fixture.OutboxMessages.CountDocumentsAsync(message => message.EventType == eventType);
}
