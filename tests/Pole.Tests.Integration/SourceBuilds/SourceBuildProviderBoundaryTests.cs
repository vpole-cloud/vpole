using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ContainerRegistries.Application.Mongo;
using Pole.Features.ContainerRegistries.Application.Projects;
using Pole.Features.ContainerRegistries.Application.Provisioning;
using Pole.Features.ContainerRegistries.Contracts;
using Pole.Features.ContainerRegistries.Domain;
using Pole.Features.GitProviders.Application;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Connections;
using Pole.Features.GitProviders.Application.ProviderOperations;
using Pole.Features.GitProviders.Application.Repositories;
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
            "github-test",
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
        var repository = SourceRepositoryFactory.Create(
            TestData.TenantId,
            "application",
            connection.Id,
            "provider-repository-1",
            "lsbV/sample",
            "https://example.test/lsbV/sample",
            true,
            "main",
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

        Assert.IsType<SourceRepositoryBuildNotFound>(crossTenant.Value);
        var found = Assert.IsType<SourceRepositoryBuildSnapshot>(snapshot.Value);
        Assert.Equal(repository.Id, found.Id);
        Assert.Equal(repository.TechnicalName, found.TechnicalName);
        Assert.Equal(repository.Revision, found.Revision);
        Assert.Equal(repository.ConnectionId, found.ConnectionId);

        await using var destination = new MemoryStream();
        var crossTenantDownload = await archive.InvokeAsync(
            new DownloadSourceRepositoryArchiveRequest(
                TenantId: ObjectId.GenerateNewId().ToString(),
                SourceRepositoryId: repository.Id,
                CommitSha: "exact-commit",
                Destination: destination));
        var missingDownload = Assert.IsType<DownloadSourceRepositoryArchiveError>(crossTenantDownload.Value);
        Assert.IsType<SourceRepositoryBuildNotFound>(missingDownload.Value);
        Assert.Equal(0, archiveDownloader.DownloadCalls);

        var download = await archive.InvokeAsync(
            new DownloadSourceRepositoryArchiveRequest(
                TenantId: TestData.TenantId,
                SourceRepositoryId: repository.Id,
                CommitSha: "exact-commit",
                Destination: destination));
        Assert.IsType<SourceRepositoryArchiveDownloaded>(download.Value);
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
            new GetTenantRegistryArtifactRequest(
                TenantId: ObjectId.GenerateNewId().ToString(),
                RegistryProjectId: project.Id,
                RepositoryName: "web-services/service-1",
                Reference: "build-1"));

        var crossTenantError = Assert.IsType<GetTenantRegistryArtifactError>(crossTenant.Value);
        Assert.IsType<TenantContainerRegistryProjectNotFound>(crossTenantError.Value);
        Assert.Equal(0, registryClient.ArtifactCalls);

        var artifact = await service.InvokeAsync(
            new GetTenantRegistryArtifactRequest(
                TenantId: TestData.TenantId,
                RegistryProjectId: project.Id,
                RepositoryName: "web-services/service-1",
                Reference: "build-1"));

        var found = Assert.IsType<RegistryArtifact>(artifact.Value);
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

        public Task<DownloadProviderRepositoryArchiveOutcome> InvokeAsync(
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
            return Task.FromResult<DownloadProviderRepositoryArchiveOutcome>(
                new ProviderRepositoryArchiveDownloaded());
        }
    }

    private sealed class RecordingContainerRegistryClient : IContainerRegistryClient
    {
        public int ArtifactCalls { get; private set; }
        public string? ProjectName { get; private set; }

        public Task<GetRegistryArtifactOutcome> GetArtifactAsync(
            GetRegistryArtifactRequest request,
            CancellationToken cancellationToken = default)
        {
            ArtifactCalls++;
            ProjectName = request.ProjectName;
            return Task.FromResult<GetRegistryArtifactOutcome>(
                new RegistryArtifact(request.RepositoryName, request.Reference, "sha256:digest", 100));
        }

        public Task<ProvisionRegistryProjectOutcome> ProvisionProjectAsync(
            RegistryProjectProvisioningRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeleteRegistryRobotCredentialsOutcome> DeleteRobotCredentialsAsync(
            IReadOnlyCollection<RegistryRobotCleanupTarget> targets,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeleteRegistryProjectOutcome> DeleteProjectAsync(
            string externalProjectId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

}
