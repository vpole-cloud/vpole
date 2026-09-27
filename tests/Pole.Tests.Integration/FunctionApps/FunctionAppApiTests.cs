using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionAppApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Http_api_round_trips_polymorphic_trigger_contract()
    {
        var groupResponse = await Client.PostAsJsonAsync(
            "/api/v1/resource-groups/",
            TestData.CreateResourceGroupRequest("function-api").Body);
        var group = await groupResponse.Content.ReadFromJsonAsync<ResourceGroupResponse>();
        Assert.NotNull(group);

        var createResponse = await Client.PostAsJsonAsync(
            "/api/v1/function-apps/",
            new CreateFunctionAppRequestBody(
                "api-functions",
                "api-functions",
                group.Id,
                RevisionRequest(),
                ["api"]));

        Assert.Equal(HttpStatusCode.Accepted, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateFunctionAppResponse>();
        Assert.NotNull(created);

        var getResponse = await Client.GetAsync($"/api/v1/function-apps/{created.FunctionAppId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var details = await getResponse.Content.ReadFromJsonAsync<FunctionAppDetailsDto>();
        Assert.NotNull(details);
        Assert.Equal(created.RevisionId, details.FunctionApp.PendingRevisionId);
        Assert.IsType<HttpFunctionTriggerSpec>(details.PendingRevision!.Spec.Handlers[0].Trigger);
        Assert.IsType<TimerFunctionTriggerSpec>(details.PendingRevision.Spec.Handlers[1].Trigger);
    }

    [Fact]
    public async Task Create_rejects_overlapping_http_routes()
    {
        var groupResponse = await Client.PostAsJsonAsync(
            "/api/v1/resource-groups/",
            TestData.CreateResourceGroupRequest("function-validation").Body);
        var group = await groupResponse.Content.ReadFromJsonAsync<ResourceGroupResponse>();
        Assert.NotNull(group);
        var spec = RevisionRequest() with
        {
            Handlers =
            [
                new FunctionHandlerRequest(
                    "first",
                    1,
                    new HttpFunctionTriggerRequest("orders/{id}", ["POST"], FunctionExposure.Internal)),
                new FunctionHandlerRequest(
                    "second",
                    1,
                    new HttpFunctionTriggerRequest("orders/{orderId}", ["post"], FunctionExposure.Public))
            ]
        };

        var response = await Client.PostAsJsonAsync(
            "/api/v1/function-apps/",
            new CreateFunctionAppRequestBody(
                "invalid-functions",
                "invalid-functions",
                group.Id,
                spec,
                []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_returns_validation_problem_for_null_nested_spec()
    {
        var groupResponse = await Client.PostAsJsonAsync(
            "/api/v1/resource-groups/",
            TestData.CreateResourceGroupRequest("function-null-validation").Body);
        var group = await groupResponse.Content.ReadFromJsonAsync<ResourceGroupResponse>();
        Assert.NotNull(group);
        var invalidSpec = RevisionRequest() with { Image = null! };

        var response = await Client.PostAsJsonAsync(
            "/api/v1/function-apps/",
            new CreateFunctionAppRequestBody(
                "null-functions",
                "null-functions",
                group.Id,
                invalidSpec,
                []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Missing_resource_deployment_maps_to_conflict_for_delete()
    {
        var groupResponse = await Client.PostAsJsonAsync(
            "/api/v1/resource-groups/",
            TestData.CreateResourceGroupRequest("function-missing").Body);
        Assert.Equal(HttpStatusCode.Created, groupResponse.StatusCode);
        var group = await groupResponse.Content.ReadFromJsonAsync<ResourceGroupResponse>();
        Assert.NotNull(group);
        var createResponse = await Client.PostAsJsonAsync(
            "/api/v1/function-apps/",
            new CreateFunctionAppRequestBody(
                "missing-deployment-functions",
                "missing-deployment-functions",
                group.Id,
                RevisionRequest(),
                []));
        Assert.True(
            createResponse.StatusCode == HttpStatusCode.Accepted,
            await createResponse.Content.ReadAsStringAsync());
        var created = await createResponse.Content.ReadFromJsonAsync<CreateFunctionAppResponse>();
        Assert.NotNull(created);

        await Fixture.DbContext.ResourceDeployments.DeleteManyAsync(
            deployment => deployment.ResourceId == created.FunctionAppId);

        var response = await Client.DeleteAsync($"/api/v1/function-apps/{created.FunctionAppId}");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "function-apps.resource-deployment.missing",
            problem.RootElement.GetProperty("code").GetString());
    }

    private static FunctionRevisionRequest RevisionRequest() =>
        new(
            new FunctionImageRequest("registry.example", "team/functions", "v1"),
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, string>.Empty),
            new FunctionResources(
                new FunctionResourceValues(100, 128),
                new FunctionResourceValues(500, 512)),
            new FunctionScalingPolicy(0, 5, 4, 8, 60, 30),
            [
                new FunctionHandlerRequest(
                    "webhook",
                    4,
                    new HttpFunctionTriggerRequest("orders/{id}", ["POST"], FunctionExposure.Public)),
                new FunctionHandlerRequest(
                    "cleanup",
                    1,
                    new TimerFunctionTriggerRequest(
                        "*/5 * * * *",
                        TimerConcurrencyPolicy.Forbid,
                        TimerMisfirePolicy.FireOnce,
                        new FunctionRetryPolicy(3, [5, 30])))
            ]);

    private sealed record ResourceGroupResponse(string Id);
}
