using System.Collections.Immutable;
using System.Net;
using MongoDB.Driver;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Domain;
using Pole.Features.Quotas.Domain;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Http;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

[Collection(IntegrationTestCollection.Global4)]
public sealed class DeploymentTargetAdmissionGateTests(
    MongoReplicaSetFixture fixture
) : IsolatedApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task RejectingDefaultTarget_BlocksWorkloadsButNotLogicalResourceGroups()
    {
        var groupResponse = await Client.PostAsJsonAsync(
            "/api/v1/resource-groups/",
            Data.CreateResourceGroupRequest("admission-base").Body);
        Assert.Equal(HttpStatusCode.Created, groupResponse.StatusCode);
        var group = await groupResponse.Content.ReadFromJsonAsync<ResourceGroupResponse>();
        Assert.NotNull(group);

        RejectNewDeploymentTargetWorkloads();

        var logicalGroupResponse = await Client.PostAsJsonAsync(
            "/api/v1/resource-groups/",
            Data.CreateResourceGroupRequest("admission-rejected").Body);
        Assert.Equal(HttpStatusCode.Created, logicalGroupResponse.StatusCode);
        var baseline = await CaptureDurableCountsAsync();

        await AssertRejectedAsync(
            "/api/v1/web-services/",
            Data.CreateWebServiceRequest(group.Id, "admission-web", "admission.example.com").Body);
        await AssertRejectedAsync(
            "/api/v1/function-apps/",
            new CreateFunctionAppRequestBody(
                "admission-functions",
                "admission-functions",
                group.Id,
                CreateFunctionRevision(),
                ["admission"]));

        Assert.Equal(baseline, await CaptureDurableCountsAsync());
    }

    private async Task AssertRejectedAsync<TBody>(string path, TBody body)
    {
        var response = await Client.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private async Task<DurableCounts> CaptureDurableCountsAsync() => new(
        await Fixture.DbContext.ResourceGroups.CountDocumentsAsync(Builders<ResourceGroup>.Filter.Empty),
        await Fixture.Database.WebServices().CountDocumentsAsync(Builders<WebService>.Filter.Empty),
        await Fixture.DbContext.FunctionApps.CountDocumentsAsync(Builders<FunctionApp>.Filter.Empty),
        await Fixture.DbContext.FunctionRevisions.CountDocumentsAsync(Builders<FunctionRevision>.Filter.Empty),
        await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty),
        await Fixture.Database
            .GetCollection<QuotaReservation>("QuotaReservations")
            .CountDocumentsAsync(Builders<QuotaReservation>.Filter.Empty));

    private static FunctionRevisionRequest CreateFunctionRevision() => new(
        new FunctionImageRequest("registry.example", "team/functions", "v1"),
        new FunctionConfiguration(
            ImmutableDictionary<string, string>.Empty,
            ImmutableDictionary<string, FunctionSecretReference>.Empty),
        new FunctionResources(new FunctionResourceValues(100, 128), new FunctionResourceValues(500, 512)),
        new FunctionScalingPolicy(0, 5, 4, 8, 60, 30),
        [
            new FunctionHandlerRequest(
                "webhook",
                4,
                new HttpFunctionTriggerRequest("orders/{id}", ["POST"], FunctionExposure.Public)),
        ]);

    private sealed record DurableCounts(
        long ResourceGroups,
        long WebServices,
        long FunctionApps,
        long FunctionRevisions,
        long OutboxMessages,
        long QuotaReservations
    );

    private sealed record ResourceGroupResponse(
        string Id
    );
}
