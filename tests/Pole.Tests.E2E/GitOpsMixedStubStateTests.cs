// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Formats.Tar;
using System.IO.Compression;
using Pole.Tests.E2E.GitHubStub;
using Xunit;

namespace Pole.Tests.E2E;

public sealed class GitOpsMixedStubStateTests
{
    private static readonly string First = new('d', 40);
    private static readonly string Second = new('e', 40);

    [Fact]
    public void CommitsRemainImmutableWhileTheBranchHeadMoves()
    {
        var state = new GitOpsMixedStubState();
        var original = new GitOpsMixedSnapshotRequest(
            First,
            [new GitOpsMixedFile("manifests/b.yaml", "second"), new GitOpsMixedFile("manifests/a.yml", "first")]);
        Assert.Equal(GitOpsMixedPublishResult.Published, state.Publish(original));
        Assert.Equal(
            GitOpsMixedPublishResult.AlreadyPresent,
            state.Publish(original with { Files = original.Files.Reverse().ToArray() }));
        Assert.Equal(
            GitOpsMixedPublishResult.Published,
            state.Publish(
                new GitOpsMixedSnapshotRequest(Second, [new GitOpsMixedFile("manifests/new.yaml", "next input")])));
        Assert.Equal(Second, state.Head);
        Assert.Equal(["manifests/a.yml", "manifests/b.yaml"], state.Find(First)!.Files.Select(file => file.Path));
        Assert.Equal(
            GitOpsMixedPublishResult.CommitConflict,
            state.Publish(
                new GitOpsMixedSnapshotRequest(First, [new GitOpsMixedFile("manifests/a.yml", "changed input")])));
        Assert.Equal(Second, state.Head);
        Assert.Equal("first", state.Find(First)!.Files[0].Content);
        Assert.Null(state.Find("main"));
    }

    [Fact]
    public void TreeAndArchiveHaveDeterministicOrderingAndCompleteNestedPaths()
    {
        var state = new GitOpsMixedStubState();
        var longPath = "manifests/" + new string('f', 110) + "/service.yaml";
        Assert.Equal(
            GitOpsMixedPublishResult.Published,
            state.Publish(
                new GitOpsMixedSnapshotRequest(
                    First,
                    [
                        new GitOpsMixedFile("README.md", "outside manifest directory"),
                        new GitOpsMixedFile(longPath, "nested"),
                        new GitOpsMixedFile("manifests/group.yml", "group"),
                    ])));
        var snapshot = state.Find(First)!;
        var tree = snapshot.Tree();
        Assert.Equal(tree.Select(item => item.Path).Order(StringComparer.Ordinal), tree.Select(item => item.Path));
        Assert.Contains(tree, item => item.Path == "manifests" && item.Type == "tree" && item.Mode == "040000");
        Assert.Contains(tree, item => item.Path == longPath && item.Type == "blob" && item.Mode == "100644");
        Assert.Equal(snapshot.Archive(), snapshot.Archive());

        using var data = new MemoryStream(snapshot.Archive());
        using var gzip = new GZipStream(data, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        while (tar.GetNextEntry() is { } entry)
        {
            Assert.StartsWith($"repo-{First}", entry.Name, StringComparison.Ordinal);
            if (entry.EntryType == TarEntryType.Directory)
                continue;

            Assert.Equal(TarEntryType.RegularFile, entry.EntryType);
            using var content = new StreamReader(entry.DataStream!, leaveOpen: true);
            files.Add(entry.Name[$"repo-{First}/".Length..], content.ReadToEnd());
        }

        Assert.Equal(snapshot.Files.Select(file => new KeyValuePair<string, string>(file.Path, file.Content)), files);
    }

    [Theory]
    [InlineData("/absolute.yaml")]
    [InlineData("manifests/../escape.yaml")]
    [InlineData("manifests//group.yaml")]
    [InlineData("manifests\\group.yaml")]
    [InlineData("./group.yaml")]
    public void UnsafePathsDoNotPublishACommit(string path)
    {
        var state = new GitOpsMixedStubState();
        Assert.Equal(
            GitOpsMixedPublishResult.InvalidInput,
            state.Publish(new GitOpsMixedSnapshotRequest(First, [new GitOpsMixedFile(path, "input")])));
        Assert.Null(state.Head);
        Assert.Null(state.Find(First));
    }

    [Fact]
    public void InvalidCommitsAndAmbiguousTreePathsAreRejected()
    {
        var state = new GitOpsMixedStubState();
        Assert.Equal(
            GitOpsMixedPublishResult.InvalidInput,
            state.Publish(
                new GitOpsMixedSnapshotRequest("main", [new GitOpsMixedFile("manifests/group.yaml", "input")])));
        Assert.Equal(
            GitOpsMixedPublishResult.InvalidInput,
            state.Publish(
                new GitOpsMixedSnapshotRequest(
                    First,
                    [new GitOpsMixedFile("manifests/a.yaml", "a"), new GitOpsMixedFile("manifests/a.yaml", "a")])));
        Assert.Equal(
            GitOpsMixedPublishResult.InvalidInput,
            state.Publish(
                new GitOpsMixedSnapshotRequest(
                    First,
                    [new GitOpsMixedFile("manifests", "file"), new GitOpsMixedFile("manifests/a.yaml", "nested")])));
        Assert.Null(state.Head);
    }

    [Fact]
    public void SnapshotInputIsBoundedAndInvalidUtf8IsRejected()
    {
        var state = new GitOpsMixedStubState();
        Assert.Equal(
            GitOpsMixedPublishResult.InvalidInput,
            state.Publish(
                new GitOpsMixedSnapshotRequest(
                    First,
                    Enumerable.Range(0, 257)
                        .Select(index => new GitOpsMixedFile($"{index}.yaml", "input"))
                        .ToArray())));
        Assert.Equal(
            GitOpsMixedPublishResult.InvalidInput,
            state.Publish(
                new GitOpsMixedSnapshotRequest(
                    First,
                    [new GitOpsMixedFile("a.yaml", new string('x', 1024 * 1024 + 1))])));
        Assert.Equal(
            GitOpsMixedPublishResult.InvalidInput,
            state.Publish(new GitOpsMixedSnapshotRequest(First, [new GitOpsMixedFile("a.yaml", "\ud800")])));
        Assert.Null(state.Head);
    }

    [Fact]
    public async Task ConcurrentPublishCannotReplaceAnImmutableSnapshot()
    {
        var state = new GitOpsMixedStubState();
        var results = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(index => Task.Run(() => state.Publish(
                    new GitOpsMixedSnapshotRequest(
                        First,
                        [new GitOpsMixedFile("manifests/group.yaml", index.ToString())])))));
        Assert.Equal(1, results.Count(result => result == GitOpsMixedPublishResult.Published));
        Assert.Equal(7, results.Count(result => result == GitOpsMixedPublishResult.CommitConflict));
        Assert.Equal(First, state.Head);
        Assert.Single(state.Find(First)!.Files);
    }
}
