using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Pole.Tests.E2E.GitHubStub;

public sealed class GitOpsStubState
{
    private string revision = "v1";
    public string Revision => Volatile.Read(ref revision);

    public bool Set(string value)
    {
        if (value is not ("v1" or "v2" or "v3" or "deleted"))
            return false;
        Interlocked.Exchange(ref revision, value);
        return true;
    }

    public static string Commit(string version) => new(
        version switch
        {
            "v1" => 'a',
            "v2" => 'b',
            "v3" => 'c',
            _ => throw new ArgumentOutOfRangeException(nameof(version)),
        },
        40);

    public static string? Version(string commit) =>
        new[] { "v1", "v2", "v3" }.SingleOrDefault(item => Commit(item) == commit);

    public static byte[] Archive(string commit)
    {
        var version = Version(commit) ?? throw new ArgumentException("Unknown fixture commit.", nameof(commit));
        var yaml = $"""
                    apiVersion: pole.io/v1alpha1
                    kind: ResourceGroup
                    metadata:
                      name: gitops-e2e
                    spec:
                      description: GitOps E2E {version}
                    """;
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        using (var content = new MemoryStream(Encoding.UTF8.GetBytes(yaml)))
        {
            tar.WriteEntry(
                new PaxTarEntry(TarEntryType.RegularFile, $"repo-{commit}/manifests/group.yaml")
                    { DataStream = content });
        }

        return output.ToArray();
    }
}

public static class GitOpsStubEndpoints
{
    public static void MapGitOpsStub(this WebApplication app, Func<HttpRequest, IConfiguration, bool> authorized)
    {
        app.MapPost(
            "/__e2e/gitops-revision",
            (
                    HttpRequest request, RevisionRequest body,
                    GitOpsStubState state, IConfiguration config) => !authorized(request, config)
                    ?
                    Results.Unauthorized()
                    :
                    state.Set(body.Revision)
                        ? Results.NoContent()
                        : Results.BadRequest());
        app.MapGet(
            "/repositories/9002",
            (HttpRequest request, GitHubStubState journal) =>
            {
                journal.Record(request);
                return Results.Ok(
                    new
                    {
                        id = 9002,
                        name = "gitops-sample",
                        full_name = "pole-e2e/gitops-sample",
                        html_url = $"{request.Scheme}://{request.Host}/pole-e2e/gitops-sample",
                        @private = true,
                        default_branch = "main",
                        updated_at = "2026-10-30T00:00:00Z",
                    });
            });
        app.MapGet(
            "/repos/pole-e2e/gitops-sample/branches/{branch}",
            (
                string branch, HttpRequest request,
                GitOpsStubState state, GitHubStubState journal) =>
            {
                journal.Record(request);
                return branch != "main" || state.Revision == "deleted"
                    ? Results.NotFound()
                    : Results.Ok(new { name = "main", commit = new { sha = GitOpsStubState.Commit(state.Revision) } });
            });
        app.MapGet(
            "/repos/pole-e2e/gitops-sample/git/commits/{commit}",
            (
                string commit, HttpRequest request,
                GitHubStubState journal) =>
            {
                journal.Record(request);
                return GitOpsStubState.Version(commit) is null
                    ? Results.NotFound()
                    : Results.Ok(new { tree = new { sha = commit } });
            });
        app.MapGet(
            "/repos/pole-e2e/gitops-sample/git/trees/{tree}",
            (
                string tree, HttpRequest request,
                GitHubStubState journal) =>
            {
                journal.Record(request);
                return GitOpsStubState.Version(tree) is null
                    ? Results.NotFound()
                    : Results.Ok(
                        new
                        {
                            tree = new[]
                            {
                                new { path = "manifests", type = "tree", mode = "040000" },
                                new { path = "manifests/group.yaml", type = "blob", mode = "100644" },
                            },
                            truncated = false,
                        });
            });
        app.MapGet(
            "/repos/pole-e2e/gitops-sample/tarball/{commit}",
            (
                string commit, HttpRequest request,
                GitHubStubState journal) =>
            {
                journal.Record(request);
                return GitOpsStubState.Version(commit) is null
                    ? Results.NotFound()
                    : Results.File(GitOpsStubState.Archive(commit), "application/gzip");
            });
    }
}
