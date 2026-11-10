using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using Pole.Features.ResourceGroups.Testing;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Domain;
using Pole.Mediator;
using Pole.Testing;
using Xunit;

namespace Pole.Features.WebServices.Tests.Integration;

[Collection(nameof(WebServicesMongoCollection))]
public sealed class WebServiceQueryTests(
    WebServicesMongoFixture fixture
) : IAsyncLifetime
{
    private CreateWebServicePlatform _platform = null!;

    public async Task InitializeAsync()
    {
        await fixture.ResetDatabaseAsync();
        var group = await new ResourceGroupTestData(fixture.Database).CreateAsync(TestPlatformDefaults.TenantId);
        _platform = new CreateWebServicePlatform(group);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetList_FiltersBySearch_AndAppliesPagination()
    {
        using var services = WebServicesTestHost.Create(fixture, _platform);
        using var scope = services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        (await mediator.Send(WebServiceTestData.Create(_platform.GroupId, "api-alpha"))).AssertSuccess();
        (await mediator.Send(WebServiceTestData.Create(_platform.GroupId, "api-beta"))).AssertSuccess();
        var result = await mediator.Send(
            new GetWebServicesRequest
            {
                TenantId = TestPlatformDefaults.TenantId,
                Search = "beta",
                PageNumber = 1,
                PageSize = 1,
            });
        var page = result.AssertSuccess();
        var service = Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal("api-beta", service.Name);
        Assert.Single(service.Deployments);
    }

    [Fact]
    public async Task GetList_OrdersPagesByNameAndExcludesOtherTenants()
    {
        var zed = Seed("zed", TestPlatformDefaults.TenantId);
        var alpha = Seed("alpha", TestPlatformDefaults.TenantId);
        var foreign = Seed("a-foreign", ObjectId.GenerateNewId().ToString());
        await fixture.WebServices.InsertManyAsync([zed, alpha, foreign]);
        using var services = WebServicesTestHost.Create(fixture, _platform);
        using var scope = services.CreateScope();
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var request = new GetWebServicesRequest
            { TenantId = TestPlatformDefaults.TenantId, PageNumber = 1, PageSize = 1 };
        var first = (await mediator.Send(request)).AssertSuccess();
        var second = (await mediator.Send(request with { PageNumber = 2 })).AssertSuccess();
        Assert.Equal(2, first.TotalCount);
        Assert.Equal(alpha.Id, Assert.Single(first.Items).Id);
        Assert.Equal(zed.Id, Assert.Single(second.Items).Id);
    }

    private WebService Seed(string name, string tenantId) => WebServiceFactory.Create(
        name,
        tenantId,
        _platform.GroupId,
        _platform.IsolationScopeId,
        WebServiceTestData.Spec().ToDomain(),
        WebServiceExposure.Create(),
        WebServiceFactory.DefaultOptions);
}
