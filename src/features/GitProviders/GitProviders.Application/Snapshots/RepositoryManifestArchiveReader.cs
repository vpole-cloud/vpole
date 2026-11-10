using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.GitProviders.Application.Common;

namespace Pole.Features.GitProviders.Application.Snapshots;

internal sealed record ReadManifestArchiveRequest(
    Stream Archive,
    string Commit,
    string Directory,
    GitHubCommitTree Tree
);

internal sealed class RepositoryManifestArchiveReader(
    RepositoryArchiveOptions options
)
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<ReadRepositorySnapshotOutcome> ReadAsync(ReadManifestArchiveRequest request, CancellationToken ct)
    {
        if (request.Directory.Length > 0 && !RepositoryPaths.Safe(request.Directory))
            return Rejected(RepositorySnapshotRejection.InvalidPath);

        if (request.Tree.Truncated || request.Tree.Entries.Count > options.MaxEntries)
            return Rejected(RepositorySnapshotRejection.SizeLimit);

        if (request.Tree.Entries.Any(entry => !RepositoryPaths.Safe(entry.Path)) ||
            request.Tree.Entries.Select(entry => entry.Path).Distinct(StringComparer.Ordinal).Count() !=
            request.Tree.Entries.Count)
            return Rejected(RepositorySnapshotRejection.UnsafeArchive);

        var selected = request.Tree
            .Entries
            .Where(entry => RepositoryPaths.Selected(entry.Path, request.Directory))
            .ToArray();
        if (selected.Any(entry => entry.Type is not ("blob" or "tree") ||
                (entry.Type == "blob" && entry.Mode is not ("100644" or "100755"))))
            return Rejected(RepositorySnapshotRejection.UnsafeArchive);

        if (request.Directory.Length > 0 && !selected.Any(entry => entry.Path == request.Directory &&
                entry.Type == "tree"))
            return new RepositoryDirectoryNotFound(request.Directory);

        var expected = selected.Where(entry => entry.Type == "blob" && RepositoryPaths.Manifest(entry.Path))
            .Select(entry => entry.Path)
            .ToHashSet(StringComparer.Ordinal);
        if (expected.Count > SnapshotInputLimits.MaximumFiles)
            return Rejected(RepositorySnapshotRejection.SizeLimit);

        try
        {
            return await ReadArchiveAsync(request, expected, ct);
        }
        catch (ArchiveSizeLimitException)
        {
            return Rejected(RepositorySnapshotRejection.SizeLimit);
        }
        catch (DecoderFallbackException)
        {
            return Rejected(RepositorySnapshotRejection.InvalidManifestEncoding);
        }
        catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
        {
            return Rejected(RepositorySnapshotRejection.UnsafeArchive);
        }
    }

    private async Task<ReadRepositorySnapshotOutcome> ReadArchiveAsync(
        ReadManifestArchiveRequest request,
        HashSet<string> expected,
        CancellationToken ct)
    {
        // The owned temporary file is seekable. GZipStream alone can accept an absent trailer on some runtimes.
        if (request.Archive.Length > options.MaxCompressedBytes)
            return Rejected(RepositorySnapshotRejection.SizeLimit);

        if (request.Archive.Length < 18)
            return Rejected(RepositorySnapshotRejection.UnsafeArchive);

        request.Archive.Seek(-4, SeekOrigin.End);
        var trailer = new byte[4];
        await request.Archive.ReadExactlyAsync(trailer, ct);
        var expectedSize = BinaryPrimitives.ReadUInt32LittleEndian(trailer);
        request.Archive.Position = 0;
        using var gzip = new GZipStream(request.Archive, CompressionMode.Decompress, leaveOpen: true);
        using var decoded = new ArchiveLimitStream(gzip, options.MaxDecompressedBytes);
        using var reader = new TarReader(decoded, leaveOpen: true);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<RepositoryManifestFile>();
        string? root = null;
        long contentBytes = 0;
        var entries = 0;
        long endStart;

        while (true)
        {
            endStart = decoded.Transferred;
            TarEntry? entry;
            try
            {
                entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken: ct);
            }
            catch (Exception exception) when (exception is NotSupportedException or InvalidOperationException)
            {
                // TarReader uses these for unsupported entry types and oversized metadata blocks.
                // Keep this boundary around the parser only; unrelated storage failures must propagate.
                return Rejected(RepositorySnapshotRejection.UnsafeArchive);
            }

            if (entry is null)
                break;

            if (++entries > options.MaxEntries)
                throw new ArchiveSizeLimitException();

            if (entry.EntryType is TarEntryType.GlobalExtendedAttributes)
                continue;

            // Sparse streams synthesize holes above the counted TAR stream. GitHub source archives need no sparsity.
            if (entry is PaxTarEntry pax && pax.ExtendedAttributes.Keys.Any(key =>
                    key.StartsWith("GNU.sparse.", StringComparison.Ordinal)))
                return Rejected(RepositorySnapshotRejection.UnsafeArchive);

            var name = entry.EntryType == TarEntryType.Directory && entry.Name.EndsWith('/')
                ? entry.Name[..^1]
                : entry.Name;
            if (!RepositoryPaths.Safe(name) || !paths.Add(name))
                return Rejected(RepositorySnapshotRejection.UnsafeArchive);

            var separator = name.IndexOf('/');
            var entryRoot = separator < 0 ? name : name[..separator];
            root ??= entryRoot;
            if (root != entryRoot || (separator < 0 && entry.EntryType != TarEntryType.Directory))
                return Rejected(RepositorySnapshotRejection.UnsafeArchive);

            var path = separator < 0 ? "" : name[(separator + 1)..];
            var selected = path.Length > 0 && RepositoryPaths.Selected(path, request.Directory);
            if (selected &&
                entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile
                    or TarEntryType.V7RegularFile))
                return Rejected(RepositorySnapshotRejection.UnsafeArchive);

            if (selected && entry.EntryType != TarEntryType.Directory && RepositoryPaths.Manifest(path))
            {
                if (!expected.Remove(path))
                    return Rejected(RepositorySnapshotRejection.UnsafeArchive);

                if (entry.Length > SnapshotInputLimits.MaximumContentBytes + 3)
                    throw new ArchiveSizeLimitException();

                if (entry.DataStream is null && entry.Length != 0)
                    return Rejected(RepositorySnapshotRejection.UnsafeArchive);

                using var content = new MemoryStream();
                if (entry.DataStream is not null)
                    await entry.DataStream.CopyToAsync(content, ct);

                var bytes = content.ToArray();
                var offset = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble) ? 3 : 0;
                var text = StrictUtf8.GetString(bytes, offset, bytes.Length - offset);
                contentBytes += bytes.Length - offset;
                if (contentBytes > SnapshotInputLimits.MaximumContentBytes)
                    throw new ArchiveSizeLimitException();

                files.Add(new RepositoryManifestFile(path, text));
            }
            else if (entry.DataStream is not null)
            {
                await entry.DataStream.CopyToAsync(Stream.Null, ct);
            }
        }

        // Drain gzip to validate its trailer and count bytes outside selected content; TAR trailing padding is zero.
        var buffer = new byte[8192];
        int read;
        while ((read = await decoded.ReadAsync(buffer, ct)) > 0)
            if (buffer.AsSpan(0, read).ContainsAnyExcept((byte)0))
                return Rejected(RepositorySnapshotRejection.UnsafeArchive);

        if (root is null || expected.Count != 0 || decoded.Transferred - endStart < 1024 ||
            decoded.Transferred % 512 != 0 || unchecked((uint)decoded.Transferred) != expectedSize)
            return Rejected(RepositorySnapshotRejection.UnsafeArchive);

        return new RepositorySnapshotRead(
            request.Commit,
            files.OrderBy(file => file.Path, StringComparer.Ordinal).ToArray());
    }

    private static RepositorySnapshotRejected Rejected(RepositorySnapshotRejection reason) => new(reason);
}
