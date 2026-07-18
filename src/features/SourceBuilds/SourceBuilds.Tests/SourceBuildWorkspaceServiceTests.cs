using System.Collections.Immutable;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Execution;
using Pole.ResultType;
using Xunit;

namespace Pole.Features.SourceBuilds.Tests;

public sealed class SourceBuildWorkspaceServiceTests
{
    [Fact]
    public async Task CreateAsync_DisposesTheArchiveBeforeDeletingIt()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var service = new SourceBuildWorkspaceService(
            new ArchiveRepositoryService(CreateArchive()),
            new RepositoryArchiveExtractor(),
            new SourceBuildExecutionOptions { WorkspaceRoot = workspaceRoot.Path });

        var result = await service.CreateAsync(CreateConnection(), CreateSourceRepository(), "build-job");

        Assert.True(result.TryGetValue(out var workspace, out var failure), failure?.Message);
        await using (workspace)
        {
            Assert.False(File.Exists(Path.Combine(workspace.RootPath, "source.tar.gz")));
            Assert.True(File.Exists(Path.Combine(workspace.SourcePath, "src", "App.csproj")));
        }
    }

    private static GitProviderConnection CreateConnection()
    {
        var now = DateTime.UtcNow;
        return GitProviderConnectionFactory.Create(
            "tenant",
            GitProviderKind.GitHub,
            "installation",
            "account",
            "pole",
            GitProviderAccountType.Organization,
            GitProviderRepositorySelection.Selected,
            "user",
            new GitProviderConnectionFactory.CreateOptions("connection", now, now));
    }

    private static SourceRepository CreateSourceRepository()
    {
        var now = DateTime.UtcNow;
        var plan = new SourceBuildPlan(
            "candidate",
            SourceBuildPlanKind.DotNet,
            "App",
            ".",
            "src/App.csproj",
            SourceBuildPlanConfidence.High,
            ImmutableList<string>.Empty,
            ImmutableList<string>.Empty,
            ImmutableList<string>.Empty,
            null,
            "FROM scratch");
        return SourceRepositoryFactory.Create(
            "tenant",
            "connection",
            "repository",
            "pole/sample",
            "https://example.test/pole/sample",
            true,
            new SourceRepositoryAnalysis(
                "main",
                "commit",
                now,
                ImmutableList.Create("src/App.csproj"),
                ImmutableList.Create(plan)),
            plan,
            new SourceRepositoryFactory.CreateOptions("source", now, now));
    }

    private static byte[] CreateArchive()
    {
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("<Project />"));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "sample/src/App.csproj")
            {
                DataStream = content
            });
        }

        return archive.ToArray();
    }

    private sealed class ArchiveRepositoryService(byte[] archive) : IGitProviderRepositoryService
    {
        public Task<Result<ProviderPage<ProviderRepository>>> GetRepositoriesAsync(
            GitProviderConnection connection,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<ProviderPage<ProviderBranch>>> GetBranchesAsync(
            GitProviderConnection connection,
            string repositoryId,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<RepositorySourceSnapshot>> GetRepositorySnapshotAsync(
            GitProviderConnection connection,
            string repositoryId,
            string branch,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<Result> DownloadRepositoryArchiveAsync(
            GitProviderConnection connection,
            string repositoryId,
            string commitSha,
            Stream destination,
            CancellationToken cancellationToken = default)
        {
            await destination.WriteAsync(archive, cancellationToken);
            return Result.Success();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pole-workspace-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
