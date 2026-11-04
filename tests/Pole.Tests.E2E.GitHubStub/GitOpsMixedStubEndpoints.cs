using System.Collections.Immutable;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Pole.Tests.E2E.GitHubStub;

public sealed record GitOpsMixedFile(
    string Path,
    string Content);

public sealed record GitOpsMixedSnapshotRequest(
    string Commit,
    IReadOnlyList<GitOpsMixedFile> Files);

public enum GitOpsMixedPublishResult
{
    Published,
    AlreadyPresent,
    InvalidInput,
    CommitConflict
}

public sealed record GitOpsMixedTreeEntry(
    string Path,
    string Type,
    string Mode);

public sealed record GitOpsMixedSnapshot(
    string Commit,
    ImmutableArray<GitOpsMixedFile> Files)
{
    public ImmutableArray<GitOpsMixedTreeEntry> Tree() => Files
        .SelectMany(file => Directories(file.Path).Select(path => new GitOpsMixedTreeEntry(path, "tree", "040000"))
            .Append(new(file.Path, "blob", "100644")))
        .DistinctBy(entry => entry.Path, StringComparer.Ordinal)
        .OrderBy(entry => entry.Path, StringComparer.Ordinal).ToImmutableArray();

    public byte[] Archive()
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Gnu, leaveOpen: true))
        {
            var root = $"repo-{Commit}";
            tar.WriteEntry(Entry(TarEntryType.Directory, root));
            foreach (var entry in Tree())
            {
                var path = $"{root}/{entry.Path}";
                if (entry.Type == "tree")
                {
                    tar.WriteEntry(Entry(TarEntryType.Directory, path));
                    continue;
                }

                var file = Files.Single(file => file.Path == entry.Path);
                using var content = new MemoryStream(Encoding.UTF8.GetBytes(file.Content));
                var regular = Entry(TarEntryType.RegularFile, path);
                regular.DataStream = content;
                tar.WriteEntry(regular);
            }
        }

        return output.ToArray();
    }

    internal static IEnumerable<string> Directories(string path)
    {
        for (var separator = path.IndexOf('/'); separator >= 0; separator = path.IndexOf('/', separator + 1))
            yield return path[..separator];
    }

    private static GnuTarEntry Entry(TarEntryType type, string path) => new(type, path)
    {
        ModificationTime = DateTimeOffset.UnixEpoch,
        AccessTime = DateTimeOffset.UnixEpoch,
        ChangeTime = DateTimeOffset.UnixEpoch,
        Uid = 0,
        Gid = 0,
        UserName = string.Empty,
        GroupName = string.Empty,
        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead |
            (type == TarEntryType.Directory
                ? UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
                : UnixFileMode.None)
    };
}

public sealed class GitOpsMixedStubState
{
    private readonly object gate = new();
    private readonly Dictionary<string, GitOpsMixedSnapshot> snapshots = new(StringComparer.Ordinal);
    private string? head;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string? Head
    {
        get
        {
            lock (gate)
                return head;
        }
    }

    public GitOpsMixedPublishResult Publish(GitOpsMixedSnapshotRequest request)
    {
        if (!ValidCommit(request.Commit) || request.Files is null || request.Files.Count > 256 ||
            request.Files.Any(file => file is null || !ValidPath(file.Path) || file.Content is null))
            return GitOpsMixedPublishResult.InvalidInput;

        var files = request.Files.OrderBy(file => file.Path, StringComparer.Ordinal).ToImmutableArray();
        var paths = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        if (paths.Count != files.Length ||
            files.Any(file => GitOpsMixedSnapshot.Directories(file.Path).Any(paths.Contains)))
            return GitOpsMixedPublishResult.InvalidInput;

        try
        {
            if (files.Sum(file => (long)StrictUtf8.GetByteCount(file.Content)) > 1024 * 1024)
                return GitOpsMixedPublishResult.InvalidInput;
        }
        catch (EncoderFallbackException)
        {
            return GitOpsMixedPublishResult.InvalidInput;
        }

        var commit = request.Commit.ToLowerInvariant();
        lock (gate)
        {
            if (snapshots.TryGetValue(commit, out var existing))
            {
                if (!existing.Files.SequenceEqual(files))
                    return GitOpsMixedPublishResult.CommitConflict;

                head = commit;
                return GitOpsMixedPublishResult.AlreadyPresent;
            }

            snapshots.Add(commit, new(commit, files));
            head = commit;
            return GitOpsMixedPublishResult.Published;
        }
    }

    public GitOpsMixedSnapshot? Find(string commit)
    {
        if (!ValidCommit(commit))
            return null;

        lock (gate)
            return snapshots.GetValueOrDefault(commit.ToLowerInvariant());
    }

    private static bool ValidCommit(string? commit) => commit is { Length: 40 } && commit.All(Uri.IsHexDigit);

    private static bool ValidPath(string? path) => !string.IsNullOrWhiteSpace(path) &&
        !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl) &&
        path.Split('/').All(segment => segment is not ("" or "." or ".."));
}

public static class GitOpsMixedStubEndpoints
{
    public static void MapGitOpsMixedStub(this WebApplication app, Func<HttpRequest, IConfiguration, bool> authorized)
    {
        app.MapPost("/__e2e/gitops-mixed", (HttpRequest request, GitOpsMixedSnapshotRequest body,
            GitOpsMixedStubState state, IConfiguration config) =>
        {
            if (!authorized(request, config))
                return Results.Unauthorized();

            return state.Publish(body) switch
            {
                GitOpsMixedPublishResult.Published or GitOpsMixedPublishResult.AlreadyPresent => Results.NoContent(),
                GitOpsMixedPublishResult.CommitConflict => Results.Conflict(),
                GitOpsMixedPublishResult.InvalidInput => Results.BadRequest(),
                _ => throw new ArgumentOutOfRangeException(nameof(body))
            };
        });
        app.MapGet("/repositories/9003", (HttpRequest request, GitHubStubState journal) =>
        {
            journal.Record(request);
            return Results.Ok(new
            {
                id = 9003,
                name = "gitops-mixed",
                full_name = "pole-e2e/gitops-mixed",
                html_url = $"{request.Scheme}://{request.Host}/pole-e2e/gitops-mixed",
                @private = true,
                default_branch = "main",
                updated_at = "2026-10-30T00:00:00Z"
            });
        });
        app.MapGet("/repos/pole-e2e/gitops-mixed/branches/{branch}",
            (string branch, HttpRequest request, GitOpsMixedStubState state, GitHubStubState journal) =>
            {
                journal.Record(request);
                return branch == "main" && state.Head is { } commit
                    ? Results.Ok(new { name = "main", commit = new { sha = commit } })
                    : Results.NotFound();
            });
        app.MapGet("/repos/pole-e2e/gitops-mixed/git/commits/{commit}",
            (string commit, HttpRequest request, GitOpsMixedStubState state, GitHubStubState journal) =>
            {
                journal.Record(request);
                return state.Find(commit) is { } snapshot
                    ? Results.Ok(new { sha = snapshot.Commit, tree = new { sha = snapshot.Commit } })
                    : Results.NotFound();
            });
        app.MapGet("/repos/pole-e2e/gitops-mixed/git/trees/{tree}",
            (string tree, HttpRequest request, GitOpsMixedStubState state, GitHubStubState journal) =>
            {
                journal.Record(request);
                return state.Find(tree) is { } snapshot
                    ? Results.Ok(new { tree = snapshot.Tree(), truncated = false })
                    : Results.NotFound();
            });
        app.MapGet("/repos/pole-e2e/gitops-mixed/tarball/{commit}",
            (string commit, HttpRequest request, GitOpsMixedStubState state, GitHubStubState journal) =>
            {
                journal.Record(request);
                return state.Find(commit) is { } snapshot
                    ? Results.File(snapshot.Archive(), "application/gzip")
                    : Results.NotFound();
            });
    }
}
