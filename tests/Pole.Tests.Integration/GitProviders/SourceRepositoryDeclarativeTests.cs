using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.GitProviders.Application;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Repositories;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.Tests.Integration.GitOps;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitProviders;

[Collection(IntegrationTestCollection.Global)]
public sealed class SourceRepositoryDeclarativeTests(MongoReplicaSetFixture fixture)
    : IsolatedMediatorIntegrationTestBase(fixture)
{
    private readonly string sourceId = ObjectId.GenerateNewId().ToString();
    private readonly TestRepositorySnapshots snapshots = new();
    private bool accessDenied;
    private int resolutions;

    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddGitProvidersFeature(new ConfigurationBuilder().Build());
        services.AddSingleton<IRepositorySnapshotProvider>(snapshots);
        services.AddSingleton<ResolveProviderRepository>((request, _) =>
        {
            resolutions++;
            return Task.FromResult<ResolveProviderRepositoryOutcome>(accessDenied
                ? new SourceRepositoryAccessDenied()
                : new ResolvedProviderRepository(new(request.RepositoryId, "application", "organization/application",
                    "https://github.test/organization/application", true, request.Branch, null), new string('a', 40)));
        });
    }

    [Fact]
    public async Task CreatesWithoutAnalyzerThenNoOpsWithoutEventsOrJobsAndRechecksAccess()
    {
        await Connection();
        var first = await Apply();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, first.Status);
        var original = await DbContext.SourceRepositories.Find(item => item.TechnicalName == "application")
            .SingleAsync();
        var events = await DbContext.OutboxMessages.CountDocumentsAsync(item =>
            item.EventType == "pole.git-providers/v1/source-repository-changed");
        var previousResolutions = resolutions;
        var next = await Apply();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, next.Status);
        Assert.True(resolutions > previousResolutions);
        Assert.Equal(events, await DbContext.OutboxMessages.CountDocumentsAsync(item =>
            item.EventType == "pole.git-providers/v1/source-repository-changed"));
        var current = await DbContext.SourceRepositories.Find(item => item.Id == original.Id).SingleAsync();
        Assert.Equal(original with { ReferenceFence = current.ReferenceFence }, current);
        Assert.Equal(0, await DbContext.SourceBuildJobs.CountDocumentsAsync(_ => true));
        var run = await new RepositoryPipelineDriver(Services, Fixture, Data, snapshots).LatestAsync(sourceId);
        Assert.Equal(GitRunCompletion.AlreadyCurrent, Assert.IsType<GitRunFinished>(run.State).Completion);
        Assert.IsType<SourceRepositoryGitOwned>((await Services.GetRequiredService<DeleteSourceRepository>()(
            original.TenantId, original.Id)).Value);

        accessDenied = true;
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply()).Status);
        Assert.Equal(events, await DbContext.OutboxMessages.CountDocumentsAsync(item =>
            item.EventType == "pole.git-providers/v1/source-repository-changed"));
    }

    [Fact]
    public async Task MissingCreatedIdentityAndChangedLocationAreBlocked()
    {
        await Connection();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply()).Status);
        var original = await DbContext.SourceRepositories.Find(item => item.TechnicalName == "application")
            .SingleAsync();
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply("other")).Status);
        Assert.Equal("main", (await DbContext.SourceRepositories.Find(item => item.Id == original.Id)
            .SingleAsync()).Branch);
        await DbContext.SourceRepositories.DeleteOneAsync(item => item.Id == original.Id);
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply()).Status);
        Assert.Equal(0, await DbContext.SourceRepositories.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task ReusingConnectionNameNeverRebindsAcceptedReference()
    {
        var connection = await Connection();
        Assert.Equal(RepositoryPipelineStatus.ConfigurationApplied, (await Apply()).Status);
        await DbContext.GitProviderConnections.DeleteOneAsync(item => item.Id == connection.Id);
        var replacement = await Connection();
        Assert.NotEqual(connection.Id, replacement.Id);
        Assert.Equal(RepositoryPipelineStatus.Blocked, (await Apply()).Status);
        Assert.Equal(connection.Id, (await DbContext.SourceRepositories.Find(
            item => item.TechnicalName == "application").SingleAsync()).ConnectionId);
    }

    private Task<RepositoryPipelineResult> Apply(string branch = "main") =>
        new RepositoryPipelineDriver(Services, Fixture, Data, snapshots).SubmitAsync(new(Data.TenantId, sourceId),
        [new("application.yaml", $$"""
            apiVersion: pole.io/v1alpha1
            kind: SourceRepository
            metadata:
              name: application
            spec:
              connection: production
              repositoryId: "42"
              branch: {{branch}}
            """)], branch);

    private async Task<GitProviderConnection> Connection()
    {
        var connection = GitProviderConnectionFactory.Create(Data.TenantId, "production", GitProviderKind.GitHub,
            "42", "account", "organization", GitProviderAccountType.Organization, GitProviderRepositorySelection.All,
            Data.UserId, GitProviderConnectionFactory.DefaultOptions);
        await DbContext.GitProviderConnections.InsertOneAsync(connection);
        return connection;
    }
}
