using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Compiler;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Tests.Integration.Http;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public abstract partial class RepositorySourceApiTestBase(
    MongoReplicaSetFixture fixture,
    bool isolatedDatabase = false
) : ApiIntegrationTestBase(fixture, isolatedDatabase)
{
    private protected const string Root = "/api/v1/gitops/sources";

    private protected readonly TestRepositorySnapshots repository = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        NamedCatalogTestData.ConfigureIsolatedApi(services);
        services.AddSingleton<IRepositorySnapshotProvider>(repository);
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        using (Timings.Measure("gitops.seed.catalog"))
        {
            await Fixture.PrepareNamedCatalogAsync();
        }
    }

    private protected CreateRepositorySourceBody Body(Guid? key = null) => new(
        key ?? Guid.NewGuid(),
        repository.Provider,
        TestRepositorySnapshots.ConnectionId,
        "repo",
        "main",
        "");

    private protected async Task<RepositorySourceView> Create(Guid? key = null, ManifestFile[]? files = null)
    {
        repository.Publish("repo", files ?? DeclarativePipelineTestBase.Snapshot());
        using var response = await Client.PostAsJsonAsync(Root + "/", Body(key));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RepositorySourceView>())!;
    }

    private protected async Task Run(string source)
    {
        using var scope = ApiServices.CreateScope();
        await scope.ServiceProvider
            .GetRequiredService<RepositorySourceJob>()
            .ProcessAsync(new GitSourceIdentity(Data.TenantId, source), default);
    }

    private protected async Task<HttpResponseMessage> Sync(string source, ManifestFile[]? files = null)
    {
        if (files is not null)
            repository.Publish("repo", files);
        var response = await Client.PostAsJsonAsync(
            $"{Root}/{source}/sync",
            new SyncRepositorySourceBody(1, Guid.NewGuid()));
        if (response.StatusCode == HttpStatusCode.Accepted)
            await Run(source);
        return response;
    }

    private protected async Task<HttpResponseMessage> Retry(string source)
    {
        var current = (await Read(source))!;
        var response = await Client.PostAsJsonAsync(
            $"{Root}/{source}/retry",
            new RetryRepositorySourceBody(current.Revision, Guid.NewGuid(), current.Synchronization.LatestRunId!));
        if (response.StatusCode == HttpStatusCode.Accepted)
            await Run(source);
        return response;
    }

    private protected Task<RepositorySourceView?> Read(string source) =>
        Client.GetFromJsonAsync<RepositorySourceView>($"{Root}/{source}");

    private protected static async Task<JsonElement> AssertProblem(
        HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal($"tag:pole.dev,2026:problem:{code}", body.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        return body;
    }
}

public abstract class IsolatedRepositorySourceApiTestBase(
    MongoReplicaSetFixture fixture
) : RepositorySourceApiTestBase(fixture, true), IIsolatedIntegrationTest;
