// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Formats.Tar;
using System.IO.Compression;

namespace Pole.Features.SourceBuilds.Application.Execution.Workspace;

public sealed record SourceBuildRepositoryArchiveInvalid(
    string Message
);

public sealed record SourceBuildRepositoryArchiveExtracted;

public union ExtractRepositoryArchiveOutcome(
    SourceBuildRepositoryArchiveExtracted,
    SourceBuildRepositoryArchiveInvalid);

public delegate Task<ExtractRepositoryArchiveOutcome> ExtractRepositoryArchive(
    Stream archive,
    string destinationPath,
    long maxExtractedBytes,
    int maxExtractedFiles,
    CancellationToken cancellationToken = default);

public sealed class RepositoryArchiveExtractor
{
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public async Task<ExtractRepositoryArchiveOutcome> InvokeAsync(
        Stream archive,
        string destinationPath,
        long maxExtractedBytes,
        int maxExtractedFiles,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(destinationPath);
            var destinationRoot = Path.GetFullPath(destinationPath);
            var destinationPrefix
                = destinationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new TarReader(gzip, leaveOpen: true);
            string? archiveRoot = null;
            long extractedBytes = 0;
            var extractedFiles = 0;

            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
            {
                if (entry.EntryType is TarEntryType.GlobalExtendedAttributes or TarEntryType.ExtendedAttributes)
                    continue;
                if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile
                    or TarEntryType.V7RegularFile))
                    return InvalidArchive($"Archive entry type '{entry.EntryType}' is not allowed.");

                var relativePath = StripArchiveRoot(entry.Name, ref archiveRoot);
                if (relativePath is null)
                    return InvalidArchive("Archive entries do not share a single root directory.");
                if (relativePath.Length == 0)
                    continue;

                var fullPath = Path.GetFullPath(
                    Path.Combine(destinationRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!fullPath.StartsWith(destinationPrefix, PathComparison))
                    return InvalidArchive("Archive entry escapes the build workspace.");

                if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(fullPath);
                    continue;
                }

                extractedFiles++;
                extractedBytes = checked(extractedBytes + entry.Length);
                if (extractedFiles > maxExtractedFiles || extractedBytes > maxExtractedBytes)
                    return InvalidArchive("Repository archive exceeds the configured extraction limits.");
                if (entry.DataStream is null)
                    return InvalidArchive("Repository archive contains a file without content.");

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await using var output = new FileStream(
                    fullPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    useAsync: true);
                await entry.DataStream.CopyToAsync(output, cancellationToken);
            }

            return archiveRoot is null
                ? InvalidArchive("Repository archive is empty.")
                : new SourceBuildRepositoryArchiveExtracted();
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            return InvalidArchive("Repository archive is invalid or exceeds the configured limits.");
        }
    }

    private static string? StripArchiveRoot(string entryName, ref string? archiveRoot)
    {
        var normalized = entryName.Replace('\\', '/').TrimEnd('/');
        if (normalized.Length == 0 || normalized.StartsWith('/'))
            return null;

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            return null;

        archiveRoot ??= segments[0];
        if (!string.Equals(archiveRoot, segments[0], StringComparison.Ordinal))
            return null;
        return string.Join('/', segments.Skip(1));
    }

    private static ExtractRepositoryArchiveOutcome InvalidArchive(string message) =>
        new SourceBuildRepositoryArchiveInvalid(message);
}
