using MongoDB.Driver;
using Microsoft.Extensions.DependencyInjection;
using Pole.ASP.Helpers;
using Pole.Features.DnsRecords.Application.Consumers;
using Pole.Features.DnsRecords.Contracts;
using Pole.Features.DnsRecords.Domain;
using Pole.Features.DnsRecords.Events;
using Pole.Features.Events.Abstractions;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.WebServices;

public sealed class WebServiceIntegrationTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Create_Succeeds_WhenResourceGroupExists_AndDomainIsFree()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var request = TestData.CreateWebServiceRequest(resourceGroup.Id);

        var result = await Mediator.Send(request);

        result.TryGetValue(out var response, out var error);
        Assert.Null(error);
        Assert.NotNull(response);

        var savedWebService = await DbContext.WebServices.Find(service => service.Id == response.WebServiceId).FirstOrDefaultAsync();
        Assert.NotNull(savedWebService);

        var savedDns = await DbContext.DnsRecords.Find(record => record.Resource.Id == response.WebServiceId).FirstOrDefaultAsync();
        Assert.NotNull(savedDns);
        Assert.Equal(request.Body.DomainName, savedDns.Domain);
        Assert.IsType<Pending>(savedDns.Status);

        var outboxCount = await DbContext.CountOutboxMessagesAsync("web-service-created");
        Assert.Equal(1L, outboxCount);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("commit-domain-name"));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("web-service-deployment-requested"));

        var release = await DbContext.WebServiceReleases
            .Find(r => r.WebServiceId == response.WebServiceId && r.TenantId == TestData.TenantId)
            .SingleAsync();
        Assert.Equal(1, release.Revision);
        Assert.Equal(WebServiceReleaseAction.Create, release.Action);
        Assert.Equal(WebServiceReleaseOutcome.Pending, release.Outcome);
        Assert.Equal(TestData.UserId, release.ActorUserId);
        Assert.Equal(TestData.UserEmail, release.ActorEmail);

        var reservationService = Services.GetRequiredService<IDomainNameReservationService>();
        var commitConsumer = new CommitDomainNameReservationOnCommitDomainNameEventConsumer(reservationService);
        await commitConsumer.Handle(new CommitDomainNameEvent(
            savedDns.Id,
            response.WebServiceId,
            EventCore.Create(TestData.TenantId)));

        var committedDns = await DbContext.DnsRecords.Find(record => record.Id == savedDns.Id).FirstOrDefaultAsync();
        Assert.NotNull(committedDns);
        Assert.IsType<Committed>(committedDns.Status);
    }

    [Fact]
    public async Task Update_CreatesNewRelease_AndIncrementsActiveRevision()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id));
        Assert.True(createResult.TryGetValue(out var created, out _));

        var updatedSpec = TestData.ValidWebServiceSpec() with
        {
            Image = new ImageInfoDto("docker.io", "nginx", "1.28")
        };

        var updateResult = await Mediator.Send(new UpdateWebServiceRequest(
            created.WebServiceId,
            new UpdateWebServiceRequestBody(
                resourceGroup.Id,
                updatedSpec,
                ["stable"]),
            TestData.TenantId,
            new ClaimValue<string?>(TestData.UserId),
            new ClaimValue<string?>(TestData.UserEmail)));

        Assert.True(updateResult.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.Equal(created.WebServiceId, response.Id);

        var saved = await DbContext.WebServices
            .Find(service => service.Id == created.WebServiceId)
            .SingleAsync();
        Assert.Equal(2, saved.ActiveRevision);
        Assert.Equal("1.28", saved.Spec.Image.Tag);

        var release = await DbContext.WebServiceReleases
            .Find(r => r.WebServiceId == created.WebServiceId && r.Revision == 2)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.Update, release.Action);
        Assert.Equal(WebServiceReleaseOutcome.Pending, release.Outcome);
        Assert.Equal("1.28", release.Spec.Image.Tag);
        Assert.Equal(2L, await DbContext.CountOutboxMessagesAsync("web-service-deployment-requested"));
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("web-service-updated"));

        var historyResult = await Mediator.Send(new GetWebServiceDeploymentsRequest
        {
            Id = created.WebServiceId,
            TenantId = TestData.TenantId
        });

        Assert.True(historyResult.TryGetValue(out var history, out var historyError));
        Assert.Null(historyError);
        Assert.Equal(2, history.ActiveRevision);
        Assert.Equal([2, 1], history.Releases.Items.Select(item => item.Revision).ToArray());
    }

    [Fact]
    public async Task Redeploy_CreatesNewRelease_ForCurrentSpec()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id));
        Assert.True(createResult.TryGetValue(out var created, out _));

        var redeployResult = await Mediator.Send(new RedeployWebServiceRequest(
            created.WebServiceId,
            TestData.TenantId,
            new ClaimValue<string?>(TestData.UserId),
            new ClaimValue<string?>(TestData.UserEmail)));

        Assert.True(redeployResult.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.Equal(2, response.Revision);

        var saved = await DbContext.WebServices
            .Find(service => service.Id == created.WebServiceId)
            .SingleAsync();
        Assert.Equal(2, saved.ActiveRevision);

        var release = await DbContext.WebServiceReleases
            .Find(r => r.Id == response.ReleaseId)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.Redeploy, release.Action);
        Assert.Equal(saved.Spec.Image.Tag, release.Spec.Image.Tag);
        Assert.Equal(2L, await DbContext.CountOutboxMessagesAsync("web-service-deployment-requested"));
    }

    [Fact]
    public async Task Rollback_CreatesNewRelease_AndRestoresPreviousSpec()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id));
        Assert.True(createResult.TryGetValue(out var created, out _));

        await DbContext.WebServiceReleases.UpdateOneAsync(
            r => r.WebServiceId == created.WebServiceId && r.Revision == 1,
            Builders<WebServiceRelease>.Update
                .Set(r => r.Outcome, WebServiceReleaseOutcome.Succeeded)
                .Set(r => r.CompletedAtUtc, DateTime.UtcNow));

        var updatedSpec = TestData.ValidWebServiceSpec() with
        {
            Image = new ImageInfoDto("docker.io", "nginx", "1.28")
        };
        await Mediator.Send(new UpdateWebServiceRequest(
            created.WebServiceId,
            new UpdateWebServiceRequestBody(resourceGroup.Id, updatedSpec, []),
            TestData.TenantId,
            new ClaimValue<string?>(TestData.UserId),
            new ClaimValue<string?>(TestData.UserEmail)));

        var rollbackResult = await Mediator.Send(new RollbackWebServiceRequest(
            created.WebServiceId,
            new RollbackWebServiceRequestBody(1),
            TestData.TenantId,
            new ClaimValue<string?>(TestData.UserId),
            new ClaimValue<string?>(TestData.UserEmail)));

        Assert.True(rollbackResult.TryGetValue(out var rollback, out var error));
        Assert.Null(error);
        Assert.Equal(3, rollback.Revision);

        var saved = await DbContext.WebServices
            .Find(service => service.Id == created.WebServiceId)
            .SingleAsync();
        Assert.Equal(3, saved.ActiveRevision);
        Assert.Equal("1.27", saved.Spec.Image.Tag);

        var release = await DbContext.WebServiceReleases
            .Find(r => r.Id == rollback.ReleaseId)
            .SingleAsync();
        Assert.Equal(WebServiceReleaseAction.Rollback, release.Action);
        Assert.Equal("1.27", release.Spec.Image.Tag);
        Assert.Equal(WebServiceReleaseOutcome.Pending, release.Outcome);
    }

    [Fact]
    public async Task Rollback_DoesNotUseSucceededRelease_FromAnotherTenant()
    {
        const string otherTenantId = "507f1f77bcf86cd799439022";
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup(name: "alpha"));
        var otherResourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup(name: "alpha", tenantId: otherTenantId));

        var createResult = await Mediator.Send(TestData.CreateWebServiceRequest(
            resourceGroup.Id,
            name: "shared-api",
            domainName: "shared-one.example.com"));
        Assert.True(createResult.TryGetValue(out var created, out _));

        var otherCreateResult = await Mediator.Send(TestData.CreateWebServiceRequest(
            otherResourceGroup.Id,
            name: "shared-api",
            domainName: "shared-two.example.com",
            tenantId: otherTenantId));
        Assert.True(otherCreateResult.TryGetValue(out _, out _));

        var foreignRelease = WebServiceReleaseFactory.CreatePending(
            otherTenantId,
            created.WebServiceId,
            7,
            WebServiceReleaseAction.Imported,
            null,
            null,
            TestData.ValidWebServiceSpecSnapshot(),
            null,
            null) with
        {
            Outcome = WebServiceReleaseOutcome.Succeeded,
            CompletedAtUtc = DateTime.UtcNow
        };
        await DbContext.WebServiceReleases.InsertOneAsync(foreignRelease);

        var rollbackResult = await Mediator.Send(new RollbackWebServiceRequest(
            created.WebServiceId,
            new RollbackWebServiceRequestBody(7),
            TestData.TenantId,
            new ClaimValue<string?>(TestData.UserId),
            new ClaimValue<string?>(TestData.UserEmail)));

        Assert.False(rollbackResult.TryGetValue(out _, out var error));
        Assert.IsType<NotFoundFailure>(error);

        var crossTenantHistory = await Mediator.Send(new GetWebServiceDeploymentsRequest
        {
            Id = created.WebServiceId,
            TenantId = otherTenantId
        });
        Assert.False(crossTenantHistory.TryGetValue(out _, out var historyError));
        Assert.IsType<NotFoundFailure>(historyError);
    }

    [Fact]
    public async Task Create_ReturnsNotFound_WhenResourceGroupDoesNotExist()
    {
        var request = TestData.CreateWebServiceRequest("507f1f77bcf86cd799439011");

        var result = await Mediator.Send(request);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.IsType<NotFoundFailure>(error);
        Assert.Contains("507f1f77bcf86cd799439011", error.Message);
    }

    [Fact]
    public async Task Create_ReturnsConflict_WhenDomainAlreadyExists()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        await DbContext.InsertDnsRecordAsync(TestData.SeedDnsRecord(resourceGroup.Id, "existing-resource"));
        var request = TestData.CreateWebServiceRequest(resourceGroup.Id, domainName: "existing.example.com");

        var result = await Mediator.Send(request);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.IsType<ConflictFailure>(error);
        Assert.Contains("already in use", error.Message);
    }

    [Fact]
    public async Task GetList_FiltersBySearch_AndAppliesPagination()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());

        await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id, name: "api-alpha", domainName: "alpha.example.com"));
        await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id, name: "api-beta", domainName: "beta.example.com"));

        var result = await Mediator.Send(new GetWebServicesRequest
        {
            Search = "beta",
            PageNumber = 1,
            PageSize = 1
        });

        result.TryGetValue(out var response, out var error);
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Single(response.Items);
        Assert.Equal(1L, response.TotalCount);
        Assert.Equal("api-beta", response.Items.Single().Name);
    }

    [Fact]
    public async Task Delete_RemovesWebService_AndDnsRecord()
    {
        var resourceGroup = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var createResult = await Mediator.Send(TestData.CreateWebServiceRequest(resourceGroup.Id));
        Assert.True(createResult.TryGetValue(out var created, out _));
        var savedDns = await DbContext.DnsRecords.Find(record => record.Resource.Id == created.WebServiceId).FirstOrDefaultAsync();
        Assert.NotNull(savedDns);

        var deleteResult = await Mediator.Send(new DeleteWebServiceRequest(created.WebServiceId, TestData.TenantId));

        Assert.True(deleteResult.TryGetValue(out var response, out var error));
        Assert.Null(error);
        Assert.NotNull(response);
        Assert.Equal(created.WebServiceId, response.Id);

        var deletedService = await DbContext.WebServices.Find(service => service.Id == created.WebServiceId).FirstOrDefaultAsync();
        var retainedDns = await DbContext.DnsRecords.Find(record => record.Resource.Id == created.WebServiceId).FirstOrDefaultAsync();

        Assert.Null(deletedService);
        Assert.NotNull(retainedDns);
        Assert.Equal(1L, await DbContext.CountOutboxMessagesAsync("web-service-deleted"));

        var reservationService = Services.GetRequiredService<IDomainNameReservationService>();
        var deleteDnsConsumer = new ReleaseDomainNameReservationOnDeleteDnsRecordEventConsumer(reservationService);
        await deleteDnsConsumer.Handle(new DeleteDnsRecordEvent(
            savedDns.Id,
            EventCore.Create(TestData.TenantId)));

        var deletedDns = await DbContext.DnsRecords.Find(record => record.Resource.Id == created.WebServiceId).FirstOrDefaultAsync();
        Assert.Null(deletedDns);
    }
}


