// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Primitives;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.Snapshots;
using Xunit;

namespace Pole.Features.GitProviders.Tests;

public sealed class RepositoryManifestArchiveReaderTests
{
    [Fact]
    public async Task ReadsRecursiveYamlInOrdinalOrderWithBomAndIgnoresOutsideLinks()
    {
        using var archive = Archive(
            File("root/manifests/z.YML", "last"),
            File("root/manifests/nested/a.yaml", "\uFEFFfirst"),
            File("root/manifests/readme.txt", "not YAML"),
            Link("root/outside", TarEntryType.SymbolicLink));
        var tree = Tree("manifests/z.YML", "manifests/nested/a.yaml", "manifests/readme.txt");
        var outcome = await Read(archive, tree);

        var snapshot = Assert.IsType<RepositorySnapshotRead>(outcome.Value);
        Assert.Equal(["manifests/nested/a.yaml", "manifests/z.YML"], snapshot.Files.Select(file => file.Path));
        Assert.Equal(["first", "last"], snapshot.Files.Select(file => file.Content));
        Assert.Equal(new string('a', 40), snapshot.Commit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\uFEFF")]
    public async Task PreservesEmptyYamlFilesForCompilerValidation(string content)
    {
        using var archive = Archive(File("root/manifests/empty.yaml", content));
        var outcome = await Read(archive, Tree("manifests/empty.yaml"));

        var snapshot = Assert.IsType<RepositorySnapshotRead>(outcome.Value);
        var file = Assert.Single(snapshot.Files);
        Assert.Equal("manifests/empty.yaml", file.Path);
        Assert.Equal("", file.Content);
    }

    [Fact]
    public async Task ExistingDirectoryWithoutYamlIsEmptyButMissingDirectoryIsNot()
    {
        using var archive = Archive(File("root/manifests/readme.txt", "text"));
        var tree = Tree("manifests/readme.txt");
        Assert.Empty(Assert.IsType<RepositorySnapshotRead>((await Read(archive, tree)).Value).Files);
        Assert.IsType<RepositoryDirectoryNotFound>((await Read(archive, tree, "missing")).Value);
    }

    [Theory]
    [InlineData("root/../escape")]
    [InlineData("/absolute")]
    [InlineData("root/back\\slash")]
    [InlineData("root/C:/drive")]
    [InlineData("other/file")]
    [InlineData("root//empty")]
    public async Task RejectsUnsafePathsEvenOutsideManifestDirectory(string path)
    {
        using var archive = Archive(File("root/manifests/a.yaml", "a"), File(path, "outside"));
        AssertRejected(await Read(archive, Tree("manifests/a.yaml")), RepositorySnapshotRejection.UnsafeArchive);
    }

    [Theory]
    [InlineData(TarEntryType.SymbolicLink)]
    [InlineData(TarEntryType.HardLink)]
    public async Task RejectsSelectedLinks(TarEntryType type)
    {
        using var archive = Archive(File("root/manifests/a.yaml", "a"), Link("root/manifests/link", type));
        AssertRejected(await Read(archive, Tree("manifests/a.yaml")), RepositorySnapshotRejection.UnsafeArchive);
    }

    [Theory]
    [InlineData("commit", "160000")]
    [InlineData("blob", "120000")]
    public async Task RejectsSelectedSubmodulesAndGitSymlinks(string type, string mode)
    {
        using var archive = Archive(File("root/manifests/a.yaml", "a"));
        var tree = new GitHubCommitTree(
            [
                new GitHubTreeEntry("manifests", "tree", "040000"), new GitHubTreeEntry("manifests/link", type, mode),
            ],
            false);
        AssertRejected(await Read(archive, tree), RepositorySnapshotRejection.UnsafeArchive);
    }

    [Fact]
    public async Task RejectsDuplicatesMissingAndUnexpectedManifestFiles()
    {
        using var duplicate = Archive(File("root/manifests/a.yaml", "a"), File("root/manifests/a.yaml", "a"));
        using var missing = Archive(File("root/manifests/readme.txt", "readme"));
        using var extra = Archive(File("root/manifests/a.yaml", "a"), File("root/manifests/b.yaml", "b"));
        foreach (var archive in new[] { duplicate, missing, extra })
            AssertRejected(await Read(archive, Tree("manifests/a.yaml")), RepositorySnapshotRejection.UnsafeArchive);
    }

    [Fact]
    public async Task RejectsInvalidEncodingAndEverySizeBoundary()
    {
        using var invalid = Archive(Bytes("root/manifests/a.yaml", [0xff]));
        AssertRejected(
            await Read(invalid, Tree("manifests/a.yaml")),
            RepositorySnapshotRejection.InvalidManifestEncoding);
        using var archive = Archive(File("root/manifests/a.yaml", "value"));
        var tree = Tree("manifests/a.yaml");
        var options = new[]
        {
            new RepositoryArchiveOptions { MaxCompressedBytes = 1 },
            new RepositoryArchiveOptions { MaxDecompressedBytes = 1 },
            new RepositoryArchiveOptions { MaxEntries = 1 },
        };
        foreach (var option in options)
            AssertRejected(await Read(archive, tree, options: option), RepositorySnapshotRejection.SizeLimit);

        using var large = Archive(
            File("root/manifests/a.yaml", new string('a', SnapshotInputLimits.MaximumContentBytes + 4)));
        AssertRejected(await Read(large, tree), RepositorySnapshotRejection.SizeLimit);
        AssertRejected(await Read(archive, tree with { Truncated = true }), RepositorySnapshotRejection.SizeLimit);
        var many = Enumerable.Range(0, SnapshotInputLimits.MaximumFiles + 1)
            .Select(index => $"manifests/{index}.yaml")
            .ToArray();
        AssertRejected(await Read(archive, Tree(many)), RepositorySnapshotRejection.SizeLimit);
    }

    [Fact]
    public async Task RejectsTruncatedGzipAndTarWithoutEndMarkers()
    {
        using var valid = Archive(File("root/manifests/a.yaml", "content"));
        var compressed = valid.ToArray();
        foreach (var removed in new[] { 1, 4, 8, compressed.Length / 2 })
        {
            using var truncated = new MemoryStream(compressed[..^removed]);
            AssertRejected(await Read(truncated, Tree("manifests/a.yaml")), RepositorySnapshotRejection.UnsafeArchive);
        }

        valid.Position = 0;
        using var gzip = new GZipStream(valid, CompressionMode.Decompress);
        using var tar = new MemoryStream();
        await gzip.CopyToAsync(tar);
        using var withoutEnding = Compress(tar.ToArray()[..^1024]);
        AssertRejected(await Read(withoutEnding, Tree("manifests/a.yaml")), RepositorySnapshotRejection.UnsafeArchive);
    }

    [Theory]
    [InlineData('S', 0L)]
    [InlineData('L', 2147483648L)]
    public async Task UnsupportedTarTypesAndOversizedMetadataAreTypedRejections(char type, long length)
    {
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, leaveOpen: true))
        {
            writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "root/file"));
        }

        var bytes = tar.ToArray();
        bytes[156] = (byte)type;
        Encoding.ASCII.GetBytes(Convert.ToString(length, 8).PadLeft(11, '0') + "\0", bytes.AsSpan(124, 12));
        bytes.AsSpan(148, 8).Fill((byte)' ');
        var checksum = bytes.AsSpan(0, 512).ToArray().Sum(value => value);
        Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ", bytes.AsSpan(148, 8));
        using var archive = Compress(bytes);

        AssertRejected(
            await Read(archive, new GitHubCommitTree([], false), ""),
            RepositorySnapshotRejection.UnsafeArchive);
    }

    [Theory]
    [InlineData("root/manifests/sparse.bin")]
    [InlineData("root/outside/sparse.bin")]
    public async Task SparseFilesCannotExpandAboveTheArchiveByteLimit(string path)
    {
        var attributes = new Dictionary<string, string>
        {
            ["GNU.sparse.major"] = "1",
            ["GNU.sparse.minor"] = "0",
            ["GNU.sparse.name"] = path,
            ["GNU.sparse.realsize"] = "65536",
        };
        // Zero data segments: the packed 512-byte map represents a 64 KiB hole.
        var map = new byte[512];
        Encoding.ASCII.GetBytes("0\n", map);
        var sparse = new PaxTarEntry(TarEntryType.RegularFile, path, attributes)
        {
            DataStream = new MemoryStream(map),
        };
        using var archive = Archive(File("root/manifests/readme.txt", "text"), sparse);
        var options = new RepositoryArchiveOptions { MaxDecompressedBytes = 16384 };
        AssertRejected(
            await Read(archive, Tree("manifests/readme.txt"), options: options),
            RepositorySnapshotRejection.UnsafeArchive);
    }

    [Fact]
    public async Task CancellationIsNotConvertedToEmptySnapshot()
    {
        using var archive = Archive(File("root/manifests/a.yaml", "a"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RepositoryManifestArchiveReader(new RepositoryArchiveOptions()).ReadAsync(
                new ReadManifestArchiveRequest(archive, new string('a', 40), "manifests", Tree("manifests/a.yaml")),
                cancellation.Token));
    }

    private static Task<ReadRepositorySnapshotOutcome> Read(
        Stream archive,
        GitHubCommitTree tree,
        string directory = "manifests",
        RepositoryArchiveOptions? options = null) =>
        new RepositoryManifestArchiveReader(options ?? new RepositoryArchiveOptions()).ReadAsync(
            new ReadManifestArchiveRequest(archive, new string('a', 40), directory, tree),
            default);

    private static void AssertRejected(ReadRepositorySnapshotOutcome outcome, RepositorySnapshotRejection reason) =>
        Assert.Equal(reason, Assert.IsType<RepositorySnapshotRejected>(outcome.Value).Reason);

    internal static GitHubCommitTree Tree(params string[] files)
    {
        var directories = files.SelectMany(path => Enumerable.Range(1, path.Split('/').Length - 1)
                .Select(count => string.Join('/', path.Split('/').Take(count))))
            .Distinct(StringComparer.Ordinal);
        return new GitHubCommitTree(
            directories.Select(path => new GitHubTreeEntry(path, "tree", "040000"))
                .Concat(files.Select(path => new GitHubTreeEntry(path, "blob", "100644")))
                .ToArray(),
            false);
    }

    internal static MemoryStream Archive(params TarEntry[] entries)
    {
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, leaveOpen: true))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "root"));
            foreach (var entry in entries)
                writer.WriteEntry(entry);
        }

        return Compress(tar.ToArray());
    }

    private static MemoryStream Compress(byte[] tar)
    {
        var result = new MemoryStream();
        using (var gzip = new GZipStream(result, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(tar);
        }

        result.Position = 0;
        return result;
    }

    internal static TarEntry File(string path, string content) => Bytes(path, Encoding.UTF8.GetBytes(content));

    private static TarEntry Bytes(string path, byte[] content) => new PaxTarEntry(TarEntryType.RegularFile, path)
        { DataStream = new MemoryStream(content) };

    private static TarEntry Link(string path, TarEntryType type) =>
        new PaxTarEntry(type, path) { LinkName = "root/target" };
}
