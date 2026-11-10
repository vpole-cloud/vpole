using System.Net;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Request;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Endpoints.Configurations;
using Pole.Features.SourceBuilds.Domain;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Http;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class SourceBuildApiTests(
    MongoReplicaSetFixture fixture
) : ApiIntegrationTestBase(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualBuild_RejectsDeletingOrDeletedTargetDuringCleanupWindow(bool physicallyDeleted)
    {
        var webService = await CreateTargetAsync();
        var repository = await CreateSourceRepositoryAsync(Data.TenantId);
        await Fixture.DbContext.SourceRepositories.InsertOneAsync(repository);
        var response = await Client.PostAsJsonAsync(
            "/api/v1/source-build-configurations",
            new CreateSourceBuildConfigurationBody(
                "api-build",
                repository.Id,
                webService.Id,
                new SourceBuildRecipeDto("FROM scratch")));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var configuration = (await response.Content.ReadFromJsonAsync<SourceBuildConfigurationDto>())!;
        if (physicallyDeleted)
            await Fixture.Database.WebServices().DeleteOneAsync(x => x.Id == webService.Id);
        else
            await Fixture.Database
                .WebServices()
                .ReplaceOneAsync(x => x.Id == webService.Id, webService.BeginDeletion(DateTime.UtcNow));

        var build = await Client.PostAsync($"/api/v1/source-build-configurations/{configuration.Id}/builds", null);
        Assert.Equal(HttpStatusCode.NotFound, build.StatusCode);
        Assert.Equal(
            0,
            await Fixture.DbContext.SourceBuildJobs.CountDocumentsAsync(item => item.TenantId == Data.TenantId));
        Assert.Equal(
            0,
            await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(x =>
                x.TenantId == Data.TenantId && x.EventType == "pole.source-builds/v1/source-build-requested"));

        await Fixture.DbContext.SourceBuildConfigurations.DeleteOneAsync(x => x.Id == configuration.Id);
        var create = await Client.PostAsJsonAsync(
            "/api/v1/source-build-configurations",
            new CreateSourceBuildConfigurationBody(
                "api-build",
                repository.Id,
                webService.Id,
                new SourceBuildRecipeDto("FROM scratch")));
        Assert.Equal(HttpStatusCode.NotFound, create.StatusCode);
        Assert.Equal(
            0,
            await Fixture.DbContext.SourceBuildConfigurations.CountDocumentsAsync(item =>
                item.TenantId == Data.TenantId));
    }

    [Fact]
    public async Task ConfigurationAndManualBuild_AreTenantScopedAndStoredWithOutboxAtomically()
    {
        var webService = await CreateTargetAsync();
        var sourceRepository = await CreateSourceRepositoryAsync(Data.TenantId);
        await Fixture.DbContext.SourceRepositories.InsertOneAsync(sourceRepository);

        var createResponse = await Client.PostAsJsonAsync(
            "/api/v1/source-build-configurations",
            new CreateSourceBuildConfigurationBody(
                "api-build",
                sourceRepository.Id,
                webService.Id,
                new SourceBuildRecipeDto("FROM scratch")));
        var configuration = await createResponse.Content.ReadFromJsonAsync<SourceBuildConfigurationDto>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.NotNull(configuration);
        Assert.Equal("FROM scratch", configuration.Recipe.Dockerfile);
        Assert.Equal("api-build", configuration.TechnicalName);

        var buildResponse = await Client.PostAsync(
            $"/api/v1/source-build-configurations/{configuration.Id}/builds",
            null);
        var build = await buildResponse.Content.ReadFromJsonAsync<SourceBuildJobDto>();

        Assert.Equal(HttpStatusCode.Accepted, buildResponse.StatusCode);
        Assert.NotNull(build);
        Assert.Equal("queued", build.State.Status);
        Assert.Equal(40, build.CommitSha.Length);
        var storedBuild = await Fixture.DbContext.SourceBuildJobs.Find(item => item.Id == build.Id).SingleAsync();
        Assert.IsType<QueuedSourceBuild>(storedBuild.State);

        var outbox = await Fixture.DbContext
            .OutboxMessages
            .Find(item => item.TenantId == Data.TenantId &&
                item.EventType == "pole.source-builds/v1/source-build-requested")
            .SingleAsync();
        Assert.Contains(build.Id, outbox.PayloadJson, StringComparison.Ordinal);

        var pageResponse = await Client.GetAsync($"/api/v1/source-build-jobs?configurationId={configuration.Id}");
        var pageResponseBody = await pageResponse.Content.ReadAsStringAsync();
        Assert.True(pageResponse.IsSuccessStatusCode, pageResponseBody);
        var page = await pageResponse.Content.ReadFromJsonAsync<PaginatedResponse<SourceBuildJobDto>>();
        Assert.Equal(build.Id, Assert.Single(page!.Items).Id);
        Assert.Equal(1, page.PageNumber);

        Assert.Equal(
            HttpStatusCode.Conflict,
            (await Client.DeleteAsync($"/api/v1/source-repositories/{sourceRepository.Id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await Client.DeleteAsync($"/api/v1/web-services/{webService.Id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Conflict,
            (await Client.DeleteAsync($"/api/v1/source-build-configurations/{configuration.Id}")).StatusCode);

        var failed = storedBuild.Fail(SourceBuildPhase.Queued, "TestFailure", "Terminal test state.", DateTime.UtcNow);
        await Fixture.DbContext.SourceBuildJobs.ReplaceOneAsync(item => item.Id == failed.Id, failed);
        var boundDelete = await Client.DeleteAsync($"/api/v1/source-build-configurations/{configuration.Id}");
        Assert.Equal(HttpStatusCode.Conflict, boundDelete.StatusCode);
        Assert.Contains("source-builds.configurations.in-use", await boundDelete.Content.ReadAsStringAsync());
        Assert.Equal(
            HttpStatusCode.Accepted,
            (await Client.DeleteAsync($"/api/v1/web-services/{webService.Id}")).StatusCode);

        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client.DeleteAsync($"/api/v1/source-build-configurations/{configuration.Id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client.DeleteAsync($"/api/v1/source-repositories/{sourceRepository.Id}")).StatusCode);
    }

    [Fact]
    public async Task Configuration_RejectsCrossTenantReferencesAndDuplicateWebServiceLinks()
    {
        var webService = await CreateTargetAsync();
        var sourceRepository = await CreateSourceRepositoryAsync(Data.TenantId);
        var otherTenantSource = await CreateSourceRepositoryAsync("507f1f77bcf86cd799439099");
        await Fixture.DbContext.SourceRepositories.InsertManyAsync([sourceRepository, otherTenantSource]);

        var crossTenant = await Client.PostAsJsonAsync(
            "/api/v1/source-build-configurations",
            new CreateSourceBuildConfigurationBody(
                "api-build",
                otherTenantSource.Id,
                webService.Id,
                new SourceBuildRecipeDto("FROM scratch")));
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);

        var first = await Client.PostAsJsonAsync(
            "/api/v1/source-build-configurations",
            new CreateSourceBuildConfigurationBody(
                "api-build",
                sourceRepository.Id,
                webService.Id,
                new SourceBuildRecipeDto("FROM scratch")));
        var duplicate = await Client.PostAsJsonAsync(
            "/api/v1/source-build-configurations",
            new CreateSourceBuildConfigurationBody(
                "api-build",
                sourceRepository.Id,
                webService.Id,
                new SourceBuildRecipeDto("FROM scratch")));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task RecipeAndPagingTechnicalValidation_RunBeforeHandlers()
    {
        var invalidPage = await Client.GetAsync("/api/v1/source-build-jobs?pageNumber=0&pageSize=101");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);

        var invalidRecipe = new SourceBuildRecipeDto("");
        var invalidUpdate = await Client.PutAsJsonAsync(
            $"/api/v1/source-build-configurations/{ObjectId.GenerateNewId()}",
            new UpdateSourceBuildConfigurationBody(invalidRecipe, 1));
        Assert.Equal(HttpStatusCode.BadRequest, invalidUpdate.StatusCode);
    }

    [Fact]
    public async Task RecipeUpdateUsesExpectedRevisionAndDoesNotEnqueueBuild()
    {
        var webService = await CreateTargetAsync();
        var sourceRepository = await CreateSourceRepositoryAsync(Data.TenantId);
        await Fixture.DbContext.SourceRepositories.InsertOneAsync(sourceRepository);
        var createdResponse = await Client.PostAsJsonAsync(
            "/api/v1/source-build-configurations",
            new CreateSourceBuildConfigurationBody(
                "api-build",
                sourceRepository.Id,
                webService.Id,
                new SourceBuildRecipeDto("FROM scratch")));
        var configuration = await createdResponse.Content.ReadFromJsonAsync<SourceBuildConfigurationDto>();
        Assert.NotNull(configuration);

        var switchedCandidate = configuration.Recipe with { Dockerfile = "FROM busybox" };
        var updateResponse = await Client.PutAsJsonAsync(
            $"/api/v1/source-build-configurations/{configuration.Id}",
            new UpdateSourceBuildConfigurationBody(switchedCandidate, 1));

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<SourceBuildConfigurationDto>();
        Assert.Equal(2, updated!.Revision);
        Assert.Equal(
            0,
            await Fixture.DbContext.SourceBuildJobs.CountDocumentsAsync(item =>
                item.ConfigurationId == configuration.Id));
        var stale = await Client.PutAsJsonAsync(
            $"/api/v1/source-build-configurations/{configuration.Id}",
            new UpdateSourceBuildConfigurationBody(new SourceBuildRecipeDto("FROM scratch"), 1));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    private async Task<WebService> CreateTargetAsync()
    {
        var group = await Fixture.DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var response = await Client.PostAsJsonAsync(
            "/api/v1/web-services",
            SourceBuildTestData.CreateTargetRequest(Data, group.Id).Body);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreateWebServiceResponse>();
        return await Fixture.Database.WebServices().Find(item => item.Id == created!.WebServiceId).SingleAsync();
    }

    private async Task<SourceRepository> CreateSourceRepositoryAsync(string tenantId)
    {
        var now = DateTime.UtcNow;
        var connection = GitProviderConnectionFactory.Create(
            tenantId,
            "github-application",
            GitProviderKind.GitHub,
            ObjectId.GenerateNewId().ToString(),
            "1001",
            "lsbV",
            GitProviderAccountType.User,
            GitProviderRepositorySelection.Selected,
            Data.UserId,
            GitProviderConnectionFactory.DefaultOptions);
        await Fixture.DbContext.GitProviderConnections.InsertOneAsync(connection);
        GitHubClient.Snapshot = GitHubClient.Snapshot with { CommitSha = new string('a', 40) };
        return SourceRepositoryFactory.Create(
            tenantId,
            "application",
            connection.Id,
            "9001",
            "lsbV/pole-dotnet-sample",
            "https://github.com/lsbV/pole-dotnet-sample",
            true,
            "main",
            new SourceRepositoryFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now, now));
    }
}
