using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Domain;
using Pole.Features.SourceBuilds.Application.Common;
using Pole.ResultType;

namespace Pole.Features.SourceBuilds.Application.Execution;

public sealed class SourceBuildWorkspace : IAsyncDisposable
{
    public SourceBuildWorkspace(string rootPath, string sourcePath)
    {
        RootPath = rootPath;
        SourcePath = sourcePath;
    }

    public string RootPath { get; }
    public string SourcePath { get; }

    public ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
        }
        catch (IOException)
        {
            // A later workspace cleanup job can remove abandoned directories.
        }
        catch (UnauthorizedAccessException)
        {
            // A later workspace cleanup job can remove abandoned directories.
        }

        return ValueTask.CompletedTask;
    }
}

public interface ISourceBuildWorkspaceService
{
    Task<Result<SourceBuildWorkspace>> CreateAsync(
        GitProviderConnection connection,
        SourceRepository sourceRepository,
        string buildJobId,
        CancellationToken cancellationToken = default);
}

public sealed class SourceBuildWorkspaceService(
    IGitProviderRepositoryService repositoryService,
    IRepositoryArchiveExtractor archiveExtractor,
    SourceBuildExecutionOptions options) : ISourceBuildWorkspaceService
{
    public async Task<Result<SourceBuildWorkspace>> CreateAsync(
        GitProviderConnection connection,
        SourceRepository sourceRepository,
        string buildJobId,
        CancellationToken cancellationToken = default)
    {
        var workspaceRoot = Path.GetFullPath(options.WorkspaceRoot);
        Directory.CreateDirectory(workspaceRoot);
        var buildRoot = Path.Combine(workspaceRoot, $"{buildJobId}-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(buildRoot, "source");
        var archivePath = Path.Combine(buildRoot, "source.tar.gz");
        Directory.CreateDirectory(buildRoot);

        try
        {
            await using (var archiveFile = new FileStream(
                             archivePath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             useAsync: true))
            await using (var limitedArchive = new SizeLimitedWriteStream(archiveFile, options.MaxArchiveBytes))
            {
                var download = await repositoryService.DownloadRepositoryArchiveAsync(
                    connection,
                    sourceRepository.ExternalRepositoryId,
                    sourceRepository.CommitSha,
                    limitedArchive,
                    cancellationToken);
                if (download is Failure failure)
                {
                    await DeleteWorkspaceAsync(buildRoot);
                    return failure;
                }
            }

            await using var archive = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                useAsync: true);
            var extraction = await archiveExtractor.ExtractAsync(
                archive,
                sourcePath,
                options.MaxExtractedBytes,
                options.MaxExtractedFiles,
                cancellationToken);
            if (extraction is Failure extractionFailure)
            {
                await DeleteWorkspaceAsync(buildRoot);
                return extractionFailure;
            }

            File.Delete(archivePath);
            return new SourceBuildWorkspace(buildRoot, sourcePath);
        }
        catch (InvalidDataException)
        {
            await DeleteWorkspaceAsync(buildRoot);
            return Failures.ValidationFailure(new Dictionary<string, string[]>
            {
                ["RepositoryArchive"] = ["Repository archive exceeds the configured download limit."]
            });
        }
        catch (IOException)
        {
            await DeleteWorkspaceAsync(buildRoot);
            return Failures.Internal("The source build workspace could not be prepared.");
        }
    }

    private static Task DeleteWorkspaceAsync(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return Task.CompletedTask;
    }

    private sealed class SizeLimitedWriteStream(Stream inner, long maxBytes) : Stream
    {
        private long _written;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            inner.Write(buffer, offset, count);
            _written += count;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            await inner.WriteAsync(buffer, cancellationToken);
            _written += buffer.Length;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            GC.SuppressFinalize(this);
        }

        private void EnsureCapacity(int count)
        {
            if (count < 0 || _written > maxBytes - count)
                throw new InvalidDataException("Stream size limit exceeded.");
        }
    }
}
