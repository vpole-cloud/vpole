using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Pole.Features.GitProviders.Contracts;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.Features.SourceBuilds.Application.Execution.Workspace;
using Xunit;

namespace Pole.Features.SourceBuilds.Tests;

public sealed class SourceBuildWorkspaceServiceTests
{
    [Fact]
    public async Task CreateAsync_DisposesTheArchiveBeforeDeletingIt()
    {
        using var workspaceRoot = new TemporaryDirectory();
        var repositoryArchive = new ArchiveRepositoryArchive(CreateArchive());
        var service = new SourceBuildWorkspaceService(
            repositoryArchive.InvokeAsync,
            new RepositoryArchiveExtractor().InvokeAsync,
            new SourceBuildExecutionOptions { WorkspaceRoot = workspaceRoot.Path });

        var result = await service.InvokeAsync("tenant", "source", "commit", "build-job");

        var workspace = Assert.IsType<SourceBuildWorkspace>(result.Value);
        Assert.Equal(("tenant", "source", "commit"), repositoryArchive.Request);
        await using (workspace)
        {
            Assert.False(File.Exists(Path.Combine(workspace.RootPath, "source.tar.gz")));
            Assert.True(File.Exists(Path.Combine(workspace.SourcePath, "src", "App.csproj")));
        }
    }

    private static byte[] CreateArchive()
    {
        using var archive = new MemoryStream();
        using (var gzip = new GZipStream(archive, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("<Project />"));
            tar.WriteEntry(
                new PaxTarEntry(TarEntryType.RegularFile, "sample/src/App.csproj")
                {
                    DataStream = content,
                });
        }

        return archive.ToArray();
    }

    private sealed class ArchiveRepositoryArchive(
        byte[] archive
    )
    {
        public (string TenantId, string SourceRepositoryId, string CommitSha)? Request { get; private set; }

        public async Task<DownloadSourceRepositoryArchiveOutcome> InvokeAsync(
            DownloadSourceRepositoryArchiveRequest request,
            CancellationToken cancellationToken = default)
        {
            Request = (request.TenantId, request.SourceRepositoryId, request.CommitSha);
            await request.Destination.WriteAsync(archive, cancellationToken);
            return new SourceRepositoryArchiveDownloaded();
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
