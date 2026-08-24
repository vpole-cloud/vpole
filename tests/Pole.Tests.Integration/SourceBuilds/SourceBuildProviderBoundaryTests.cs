using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Application.Persistence;
using Pole.Features.ContainerRegistries.Application.Provisioning;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Persistence;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.SourceBuilds;

[Collection(IntegrationTestCollection.Name)]
public sealed class SourceBuildProviderBoundaryTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    [Fact]
    public async Task SourceRepositoryBuildCapabilities_AreTenantScopedAndMapTheBuildSnapshot()
    {
        var now = DateTime.UtcNow;
        var connection = GitProviderConnectionFactory.Create(
            TestData.TenantId,
            GitProviderKind.GitHub,
            "installation-1",
            "account-1",
            "pole",
            GitProviderAccountType.Organization,
            GitProviderRepositorySelection.Selected,
            TestData.UserId,
            new GitProviderConnectionFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                now,
                now));
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
        var repository = SourceRepositoryFactory.Create(
            TestData.TenantId,
            connection.Id,
            "provider-repository-1",
            "lsbV/sample",
            "https://example.test/lsbV/sample",
            true,
            new SourceRepositoryAnalysis(
                "main",
                "current-commit",
                now,
                ImmutableList.Create("src/App/App.csproj"),
                ImmutableList.Create(plan)),
            plan,
            new SourceRepositoryFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                now,
                now));
        await fixture.DbContext.GitProviderConnections.InsertOneAsync(connection);
        await fixture.DbContext.SourceRepositories.InsertOneAsync(repository);

        var archiveDownloader = new RecordingArchiveDownloader();
        LoadSourceRepository loadRepository = async (tenantId, sourceRepositoryId, cancellationToken) =>
            await fixture.DbContext.SourceRepositories
                .Find(item => item.TenantId == tenantId && item.Id == sourceRepositoryId)
                .FirstOrDefaultAsync(cancellationToken);
        LoadGitProviderConnection loadConnection = async (tenantId, connectionId, cancellationToken) =>
            await fixture.DbContext.GitProviderConnections
                .Find(item => item.TenantId == tenantId && item.Id == connectionId)
                .FirstOrDefaultAsync(cancellationToken);
        var finder = new SourceRepositoryBuildFinder(loadRepository);
        var archive = new SourceRepositoryArchiveDownloader(
            loadRepository,
            loadConnection,
            archiveDownloader.InvokeAsync);

        var crossTenant = await finder.InvokeAsync(
            ObjectId.GenerateNewId().ToString(),
            repository.Id);
        var snapshot = await finder.InvokeAsync(TestData.TenantId, repository.Id);

        var missing = TypedResultExtensions.TryGetValue(crossTenant);
        Assert.False(missing.Succeeded);
        Assert.IsType<SourceRepositoryBuildNotFound>(missing.Error!.Value);
        var found = TypedResultExtensions.TryGetValue(snapshot);
        Assert.True(found.Succeeded);
        Assert.Equal(repository.Id, found.Value!.Id);
        Assert.Equal(repository.CandidateKey, found.Value.CandidateKey);
        Assert.Equal(SourceRepositoryBuildPlanKind.DotNet, found.Value.SelectedPlan.Kind);
        Assert.Equal(plan.Commands, found.Value.SelectedPlan.Commands);
        Assert.Equal(plan.DockerfilePreview, found.Value.SelectedPlan.Dockerfile);

        await using var destination = new MemoryStream();
        var crossTenantDownload = await archive.InvokeAsync(
            ObjectId.GenerateNewId().ToString(),
            repository.Id,
            "exact-commit",
            destination);
        var missingDownload = TypedResultExtensions.TryGetValue(crossTenantDownload);
        Assert.False(missingDownload.Succeeded);
        Assert.IsType<SourceRepositoryBuildNotFound>(missingDownload.Error!.Value);
        Assert.Equal(0, archiveDownloader.DownloadCalls);

        var download = await archive.InvokeAsync(
            TestData.TenantId,
            repository.Id,
            "exact-commit",
            destination);
        Assert.True(TypedResultExtensions.TryGetValue(download).Succeeded);
        Assert.Equal(1, archiveDownloader.DownloadCalls);
        Assert.Equal(connection.Id, archiveDownloader.ConnectionId);
        Assert.Equal(repository.ExternalRepositoryId, archiveDownloader.ExternalRepositoryId);
        Assert.Equal("exact-commit", archiveDownloader.CommitSha);
    }

    [Fact]
    public async Task RegistryArtifactLookup_RevalidatesTheTenantAndDerivesTheProjectName()
    {
        var now = DateTime.UtcNow;
        var project = RegistryProjectFactory.Create(
            TestData.TenantId,
            ContainerRegistryProvider.Harbor,
            "persisted-project-name",
            TestData.UserId,
            new RegistryProjectFactory.CreateOptions(ObjectId.GenerateNewId().ToString(), now)) with
        {
            State = new ReadyRegistryProject(
                "external-project",
                "build-credential",
                "pull-credential",
                now)
        };
        await fixture.DbContext.RegistryProjects.InsertOneAsync(project);

        var registryClient = new RecordingContainerRegistryClient();
        LoadTenantHarborRegistryProject loadProject = async (tenantId, cancellationToken) =>
            await fixture.DbContext.RegistryProjects
                .Find(item => item.TenantId == tenantId && item.Provider == ContainerRegistryProvider.Harbor)
                .FirstOrDefaultAsync(cancellationToken);
        var service = new TenantRegistryArtifactLookup(loadProject, registryClient);

        var crossTenant = await service.InvokeAsync(
            ObjectId.GenerateNewId().ToString(),
            project.Id,
            "web-services/service-1",
            "build-1");

        var crossTenantResult = TypedResultExtensions.TryGetValue(crossTenant);
        Assert.False(crossTenantResult.Succeeded);
        Assert.IsType<TenantContainerRegistryProjectNotFound>(crossTenantResult.Error!.Value);
        Assert.Equal(0, registryClient.ArtifactCalls);

        var artifact = await service.InvokeAsync(
            TestData.TenantId,
            project.Id,
            "web-services/service-1",
            "build-1");

        var foundResult = TypedResultExtensions.TryGetValue(artifact);
        Assert.True(foundResult.Succeeded);
        var found = foundResult.Value!;
        Assert.Equal("sha256:digest", found.Digest);
        Assert.Equal(1, registryClient.ArtifactCalls);
        Assert.Equal(project.Name, registryClient.ProjectName);
    }

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class RecordingArchiveDownloader
    {
        public int DownloadCalls { get; private set; }
        public string? ConnectionId { get; private set; }
        public string? ExternalRepositoryId { get; private set; }
        public string? CommitSha { get; private set; }

        public Task<Result<Unit, GitProviderRepositoryAccessError>> InvokeAsync(
            GitProviderConnection connection,
            string repositoryId,
            string commitSha,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            DownloadCalls++;
            ConnectionId = connection.Id;
            ExternalRepositoryId = repositoryId;
            CommitSha = commitSha;
            return Task.FromResult<Result<Unit, GitProviderRepositoryAccessError>>(
                new Ok<Unit>(Unit.Value));
        }
    }

    private sealed class RecordingContainerRegistryClient : IContainerRegistryClient
    {
        public int ArtifactCalls { get; private set; }
        public string? ProjectName { get; private set; }

        public Task<Result<RegistryArtifact, ContainerRegistryArtifactLookupError>> GetArtifactAsync(
            string projectName,
            string repositoryName,
            string reference,
            CancellationToken cancellationToken = default)
        {
            ArtifactCalls++;
            ProjectName = projectName;
            return Task.FromResult<Result<RegistryArtifact, ContainerRegistryArtifactLookupError>>(
                new Ok<RegistryArtifact>(new RegistryArtifact(repositoryName, reference, "sha256:digest", 100)));
        }

        public Task<Result<ProvisionedRegistryProject, ContainerRegistryProviderError>> ProvisionProjectAsync(
            RegistryProjectProvisioningRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<RegistryRobotCleanupResult, ContainerRegistryProviderError>> DeleteRobotCredentialsAsync(
            IReadOnlyCollection<RegistryRobotCleanupTarget> targets,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<Unit, ContainerRegistryProviderError>> DeleteProjectAsync(
            string externalProjectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

}
