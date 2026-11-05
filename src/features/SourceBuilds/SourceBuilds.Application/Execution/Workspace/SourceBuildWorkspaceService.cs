using Pole.Features.GitProviders.Contracts;
using Pole.Features.SourceBuilds.Application.Common;

namespace Pole.Features.SourceBuilds.Application.Execution.Workspace;

public sealed record SourceBuildWorkspaceArchiveLimitExceeded;

public union CreateSourceBuildWorkspaceError(
    DownloadSourceRepositoryArchiveError,
    SourceBuildRepositoryArchiveInvalid,
    SourceBuildWorkspaceArchiveLimitExceeded);

public union CreateSourceBuildWorkspaceOutcome(
    SourceBuildWorkspace,
    CreateSourceBuildWorkspaceError);

public delegate Task<CreateSourceBuildWorkspaceOutcome>
    CreateSourceBuildWorkspace(
        string tenantId,
        string sourceRepositoryId,
        string commitSha,
        string buildJobId,
        CancellationToken cancellationToken = default);

public sealed class SourceBuildWorkspaceService(
    DownloadSourceRepositoryArchive downloadSourceRepositoryArchive,
    ExtractRepositoryArchive extractRepositoryArchive,
    SourceBuildExecutionOptions options)
{
    public async Task<CreateSourceBuildWorkspaceOutcome> InvokeAsync(
        string tenantId,
        string sourceRepositoryId,
        string commitSha,
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
                var download = await downloadSourceRepositoryArchive(
                    new DownloadSourceRepositoryArchiveRequest(
                        TenantId: tenantId,
                        SourceRepositoryId: sourceRepositoryId,
                        CommitSha: commitSha,
                        Destination: limitedArchive),
                    cancellationToken: cancellationToken);
                if (download is DownloadSourceRepositoryArchiveError failure)
                {
                    await DeleteWorkspaceAsync(buildRoot);
                    return new CreateSourceBuildWorkspaceError(failure);
                }
            }

            ExtractRepositoryArchiveOutcome extraction;
            await using (var archive = new FileStream(
                             archivePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 64 * 1024,
                             useAsync: true))
            {
                extraction = await extractRepositoryArchive(
                    archive,
                    sourcePath,
                    options.MaxExtractedBytes,
                    options.MaxExtractedFiles,
                    cancellationToken);
            }
            if (extraction is SourceBuildRepositoryArchiveInvalid extractionFailure)
            {
                await DeleteWorkspaceAsync(buildRoot);
                return new CreateSourceBuildWorkspaceError(extractionFailure);
            }

            File.Delete(archivePath);
            return new SourceBuildWorkspace(buildRoot, sourcePath);
        }
        catch (InvalidDataException)
        {
            await DeleteWorkspaceAsync(buildRoot);
            return new CreateSourceBuildWorkspaceError(new SourceBuildWorkspaceArchiveLimitExceeded());
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
