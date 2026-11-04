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

public sealed record GitOpsRepositoryAccessRequest(
    bool Denied);

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
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object gate = new();
    private readonly Dictionary<string, GitOpsMixedSnapshot> snapshots = new(StringComparer.Ordinal);
    private string? head;
    private volatile bool accessDenied;

    public bool AccessDenied
    {
        get => accessDenied;
        set => accessDenied = value;
    }

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
        MapRepository(app, authorized, "9003", "gitops-mixed", app.Services.GetRequiredService<GitOpsMixedStubState>());
        MapRepository(app, authorized, "9004", "gitops-lifecycle", new GitOpsMixedStubState());
        MapRepository(app, authorized, "9005", "gitops-redis-replacement", new GitOpsMixedStubState());
        var console = new GitOpsMixedStubState();
        MapRepository(app, authorized, "9006", "gitops-console", console);
        app.MapPost("/__e2e/gitops-console/access", (HttpRequest request, GitOpsRepositoryAccessRequest body,
            IConfiguration config) =>
        {
            if (!authorized(request, config))
                return Results.Unauthorized();

            console.AccessDenied = body.Denied;
            return Results.NoContent();
        });
    }

    public static object ConsoleRepository(string baseUrl) => Repository("9006", "gitops-console", baseUrl);

    private static object Repository(string id, string name, string baseUrl) => new
    {
        id = long.Parse(id, System.Globalization.CultureInfo.InvariantCulture),
        name,
        full_name = $"pole-e2e/{name}",
        html_url = $"{baseUrl.TrimEnd('/')}/pole-e2e/{name}",
        @private = true,
        default_branch = "main",
        updated_at = "2026-10-30T00:00:00Z"
    };

    private static void MapRepository(WebApplication app, Func<HttpRequest, IConfiguration, bool> authorized,
        string repositoryId, string name, GitOpsMixedStubState state)
    {
        var repositoryPath = $"/repos/pole-e2e/{name}";
        app.MapPost($"/__e2e/{name}", (HttpRequest request, GitOpsMixedSnapshotRequest body,
            IConfiguration config) =>
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
        app.MapGet($"/repositories/{repositoryId}", (HttpRequest request, GitHubStubState journal) =>
        {
            journal.Record(request);
            return state.AccessDenied
                ? Results.StatusCode(StatusCodes.Status403Forbidden)
                : Results.Ok(Repository(repositoryId, name, $"{request.Scheme}://{request.Host}"));
        });
        app.MapGet(repositoryPath + "/branches", (HttpRequest request, GitHubStubState journal) =>
        {
            journal.Record(request);
            if (state.AccessDenied)
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            return Results.Ok(state.Head is { } commit
                ? new[] { new { name = "main", commit = new { sha = commit }, @protected = false } }
                : []);
        });
        app.MapGet(repositoryPath + "/branches/{branch}",
            (string branch, HttpRequest request, GitHubStubState journal) =>
            {
                journal.Record(request);
                if (state.AccessDenied)
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                return branch == "main" && state.Head is { } commit
                    ? Results.Ok(new { name = "main", commit = new { sha = commit } })
                    : Results.NotFound();
            });
        app.MapGet(repositoryPath + "/git/commits/{commit}",
            (string commit, HttpRequest request, GitHubStubState journal) =>
            {
                journal.Record(request);
                if (state.AccessDenied)
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                return state.Find(commit) is { } snapshot
                    ? Results.Ok(new { sha = snapshot.Commit, tree = new { sha = snapshot.Commit } })
                    : Results.NotFound();
            });
        app.MapGet(repositoryPath + "/git/trees/{tree}",
            (string tree, HttpRequest request, GitHubStubState journal) =>
            {
                journal.Record(request);
                if (state.AccessDenied)
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                return state.Find(tree) is { } snapshot
                    ? Results.Ok(new { tree = snapshot.Tree(), truncated = false })
                    : Results.NotFound();
            });
        app.MapGet(repositoryPath + "/tarball/{commit}",
            (string commit, HttpRequest request, GitHubStubState journal) =>
            {
                journal.Record(request);
                if (state.AccessDenied)
                    return Results.StatusCode(StatusCodes.Status403Forbidden);

                return state.Find(commit) is { } snapshot
                    ? Results.File(snapshot.Archive(), "application/gzip")
                    : Results.NotFound();
            });
    }
}
