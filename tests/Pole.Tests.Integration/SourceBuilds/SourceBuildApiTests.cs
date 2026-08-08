using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Request;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Endpoints;
using Pole.Features.SourceBuilds.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

public sealed class SourceBuildApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task ConfigurationAndManualBuild_AreTenantScopedAndStoredWithOutboxAtomically()
    {
        var webService = TestData.SeedWebService(ObjectId.GenerateNewId().ToString());
        var sourceRepository = CreateSourceRepository(TestData.TenantId);
        await Fixture.Database.WebServices().InsertOneAsync(webService);
        await Fixture.DbContext.SourceRepositories.InsertOneAsync(sourceRepository);

        var createResponse = await Client.PostAsJsonAsync(
            "/api/v1/source-build-configurations",
            new CreateSourceBuildConfigurationBody(sourceRepository.Id, webService.Id));
        var configuration = await createResponse.Content.ReadFromJsonAsync<SourceBuildConfigurationDto>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.NotNull(configuration);
        Assert.Equal(sourceRepository.SelectedPlan.CandidateKey, configuration.Recipe.CandidateKey);
        Assert.False(configuration.Recipe.IsCustomized);

        var buildResponse = await Client.PostAsync(
            $"/api/v1/source-build-configurations/{configuration.Id}/builds",
            null);
        var build = await buildResponse.Content.ReadFromJsonAsync<SourceBuildJobDto>();

        Assert.Equal(HttpStatusCode.Accepted, buildResponse.StatusCode);
        Assert.NotNull(build);
        Assert.Equal("queued", build.State.Status);
        Assert.Equal(sourceRepository.CommitSha, build.CommitSha);
        var storedBuild = await Fixture.DbContext.SourceBuildJobs
            .Find(item => item.Id == build.Id)
            .SingleAsync();
        Assert.IsType<QueuedSourceBuild>(storedBuild.State);

        var outbox = await Fixture.DbContext.OutboxMessages
            .Find(item => item.TenantId == TestData.TenantId && item.EventType == "source-build-requested")
            .SingleAsync();
        Assert.Contains(build.Id, outbox.PayloadJson, StringComparison.Ordinal);

        var pageResponse = await Client.GetAsync(
            $"/api/v1/source-build-jobs?configurationId={configuration.Id}");
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

        var failed = storedBuild.Fail(
            SourceBuildPhase.Queued,
            "TestFailure",
            "Terminal test state.",
            DateTime.UtcNow);
        await Fixture.DbContext.SourceBuildJobs.ReplaceOneAsync(item => item.Id == failed.Id, failed);

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
        var webService = TestData.SeedWebService(ObjectId.GenerateNewId().ToString());
        var sourceRepository = CreateSourceRepository(TestData.TenantId);
        var otherTenantSource = CreateSourceRepository("507f1f77bcf86cd799439099");
        await Fixture.Database.WebServices().InsertOneAsync(webService);
        await Fixture.DbContext.SourceRepositories.InsertManyAsync([sourceRepository, otherTenantSource]);

        var crossTenant = await Client.PostAsJsonAsync(
            "/api/v1/source-build-configurations",
            new CreateSourceBuildConfigurationBody(otherTenantSource.Id, webService.Id));
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);

        var first = await Client.PostAsJsonAsync(
            "/api/v1/source-build-configurations",
            new CreateSourceBuildConfigurationBody(sourceRepository.Id, webService.Id));
        var duplicate = await Client.PostAsJsonAsync(
            "/api/v1/source-build-configurations",
            new CreateSourceBuildConfigurationBody(sourceRepository.Id, webService.Id));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task RecipeAndPagingTechnicalValidation_RunBeforeHandlers()
    {
        var invalidPage = await Client.GetAsync(
            "/api/v1/source-build-jobs?pageNumber=0&pageSize=101");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);

        var invalidRecipe = new SourceBuildRecipeDto(
            "candidate",
            SourceBuildRecipeKind.DotNet,
            string.Empty,
            string.Empty,
            ImmutableList<string>.Empty,
            null,
            string.Empty,
            true);
        var invalidUpdate = await Client.PutAsJsonAsync(
            $"/api/v1/source-build-configurations/{ObjectId.GenerateNewId()}",
            new UpdateSourceBuildConfigurationBody(invalidRecipe));
        Assert.Equal(HttpStatusCode.BadRequest, invalidUpdate.StatusCode);
    }

    [Fact]
    public async Task RecipeUpdate_CannotSilentlySwitchAnalysisCandidate()
    {
        var webService = TestData.SeedWebService(ObjectId.GenerateNewId().ToString());
        var sourceRepository = CreateSourceRepository(TestData.TenantId);
        await Fixture.Database.WebServices().InsertOneAsync(webService);
        await Fixture.DbContext.SourceRepositories.InsertOneAsync(sourceRepository);
        var createdResponse = await Client.PostAsJsonAsync(
            "/api/v1/source-build-configurations",
            new CreateSourceBuildConfigurationBody(sourceRepository.Id, webService.Id));
        var configuration = await createdResponse.Content.ReadFromJsonAsync<SourceBuildConfigurationDto>();
        Assert.NotNull(configuration);

        var switchedCandidate = configuration.Recipe with { CandidateKey = "DotNet:other.csproj" };
        var updateResponse = await Client.PutAsJsonAsync(
            $"/api/v1/source-build-configurations/{configuration.Id}",
            new UpdateSourceBuildConfigurationBody(switchedCandidate));

        Assert.Equal(HttpStatusCode.Conflict, updateResponse.StatusCode);
    }

    private static SourceRepository CreateSourceRepository(string tenantId)
    {
        var now = DateTime.UtcNow;
        var plan = new SourceBuildPlan(
            "DotNet:src/App/App.csproj",
            SourceBuildPlanKind.DotNet,
            "App",
            "src/App",
            "src/App/App.csproj",
            SourceBuildPlanConfidence.High,
            ImmutableList.Create("ASP.NET web SDK"),
            ImmutableList<string>.Empty,
            ImmutableList.Create("dotnet restore", "dotnet publish"),
            "dotnet App.dll",
            "FROM mcr.microsoft.com/dotnet/aspnet:10.0");
        return SourceRepositoryFactory.Create(
            tenantId,
            ObjectId.GenerateNewId().ToString(),
            "123",
            "lsbV/pole-dotnet-sample",
            "https://github.com/lsbV/pole-dotnet-sample",
            true,
            new SourceRepositoryAnalysis(
                "main",
                "abc1234567890",
                now,
                ImmutableList.Create("src/App/App.csproj"),
                ImmutableList.Create(plan)),
            plan,
            new SourceRepositoryFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                now,
                now));
    }
}
