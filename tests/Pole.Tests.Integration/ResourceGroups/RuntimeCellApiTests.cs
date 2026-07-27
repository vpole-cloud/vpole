using System.Net;
using System.Net.Http.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ResourceGroups.Application.Common;
using Pole.Features.ResourceGroups.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.ResourceGroups;

public sealed class RuntimeCellApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task OperationalEndpoints_ListGetAndRetryFailedCell()
    {
        var resourceGroup = await Fixture.DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup());
        var initialTime = DateTime.UtcNow.AddMinutes(-2);
        var provisioning = RuntimeCellFactory.Create(
            resourceGroup.TenantId,
            resourceGroup.Id,
            resourceGroup.DefaultIsolationScopeId,
            "507f1f77bcf86cd799439071",
            "pole-runtime-api",
            new RuntimeCellFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                initialTime));
        var failed = provisioning.MarkFailed(
            provisioning.DesiredGeneration,
            RuntimeCellOperation.Reconciliation,
            "NamespaceOperationFailed",
            "Internal provider details must stay private.",
            initialTime.AddMinutes(1),
            initialTime.AddMinutes(1).AddSeconds(30));
        await Fixture.DbContext.RuntimeCells.InsertOneAsync(failed);

        using var listResponse = await Client.GetAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/runtime-cells");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<List<RuntimeCellDto>>();
        var listed = Assert.Single(list!);
        Assert.Equal("failed", listed.State);
        Assert.Equal("NamespaceOperationFailed", listed.FailureCode);

        using var getResponse = await Client.GetAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/runtime-cells/{failed.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        using var retryResponse = await Client.PostAsync(
            $"/api/v1/resource-groups/{resourceGroup.Id}/runtime-cells/{failed.Id}/retry",
            content: null);
        Assert.Equal(HttpStatusCode.Accepted, retryResponse.StatusCode);
        var retried = await retryResponse.Content.ReadFromJsonAsync<RuntimeCellDto>();
        Assert.NotNull(retried);
        Assert.Equal("provisioning", retried.State);
        Assert.Equal(failed.DesiredGeneration + 1, retried.DesiredGeneration);
        Assert.Null(retried.FailureCode);
        Assert.Null(retried.RetryNotBeforeUtc);

        Assert.Equal(1L, await Fixture.DbContext.CountOutboxMessagesAsync(
            "runtime-cell-reconciliation-requested"));
    }

    [Fact]
    public async Task OperationalEndpoints_ValidateIds_AndEnforceResourceGroupOwnership()
    {
        var resourceGroup = await Fixture.DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup());
        var otherResourceGroup = await Fixture.DbContext.InsertResourceGroupAsync(
            TestData.SeedResourceGroup("other"));
        var cell = RuntimeCellFactory.Create(
            resourceGroup.TenantId,
            resourceGroup.Id,
            resourceGroup.DefaultIsolationScopeId,
            "507f1f77bcf86cd799439071",
            "pole-runtime-api",
            RuntimeCellFactory.DefaultOptions);
        await Fixture.DbContext.RuntimeCells.InsertOneAsync(cell);

        using var malformed = await Client.GetAsync(
            "/api/v1/resource-groups/not-an-id/runtime-cells");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

        using var crossGroup = await Client.GetAsync(
            $"/api/v1/resource-groups/{otherResourceGroup.Id}/runtime-cells/{cell.Id}");
        Assert.Equal(HttpStatusCode.NotFound, crossGroup.StatusCode);
    }
}
