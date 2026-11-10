using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Pole.Tests.E2E.GitHubStub;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<GitHubStubState>();
builder.Services.AddSingleton<GitOpsStubState>();
builder.Services.AddSingleton<GitOpsMixedStubState>();

var app = builder.Build();
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));

app.MapPost(
    "/__e2e/reset",
    (
        HttpRequest request, GitHubStubState state,
        IConfiguration configuration) =>
    {
        if (!IsAuthorized(request, configuration))
            return Results.Unauthorized();

        state.Reset();
        return Results.NoContent();
    });

app.MapPost(
    "/__e2e/revision",
    (
        HttpRequest request, RevisionRequest body,
        GitHubStubState state, IConfiguration configuration) =>
    {
        if (!IsAuthorized(request, configuration))
            return Results.Unauthorized();

        return state.SetRevision(body.Revision)
            ? Results.NoContent()
            : Results.BadRequest(new { error = "Unknown revision." });
    });

app.MapGet(
    "/__e2e/requests",
    (
            HttpRequest request, GitHubStubState state,
            IConfiguration configuration) =>
        !IsAuthorized(request, configuration) ? Results.Unauthorized() : Results.Ok(state.Requests));

app.MapGet(
    "/apps/{slug}/installations/new",
    (
        string slug, HttpRequest request,
        IConfiguration configuration, GitHubStubState state) =>
    {
        state.Record(request);
        var callbackBaseUrl = configuration["GitHubStub:CallbackBaseUrl"] ??
            throw new InvalidOperationException("GitHubStub:CallbackBaseUrl is required.");
        var callbackUrl = QueryHelpers.AddQueryString(
            $"{callbackBaseUrl.TrimEnd('/')}/api/v1/integrations/github/setup-complete",
            new Dictionary<string, string?>
            {
                ["installation_id"] = "42",
                ["setup_action"] = "install",
                ["state"] = request.Query["state"],
            });
        return Results.Redirect(callbackUrl);
    });

app.MapGet(
    "/login/oauth/authorize",
    (HttpRequest request, GitHubStubState state) =>
    {
        state.Record(request);
        var redirectUri = request.Query["redirect_uri"].ToString();
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out _))
            return Results.BadRequest(new { error = "redirect_uri is required." });

        return Results.Redirect(
            QueryHelpers.AddQueryString(
                redirectUri,
                new Dictionary<string, string?>
                {
                    ["code"] = "e2e-oauth-code",
                    ["state"] = request.Query["state"],
                }));
    });

app.MapPost(
    "/login/oauth/access_token",
    (HttpRequest request, GitHubStubState state) =>
    {
        state.Record(request);
        return Results.Ok(new { access_token = "e2e-user-token" });
    });

app.MapGet(
    "/app/installations/42",
    (HttpRequest request, GitHubStubState state) =>
    {
        state.Record(request);
        return Results.Ok(
            new
            {
                id = 42,
                account = new { id = 42, login = "pole-e2e", type = "Organization" },
                repository_selection = "selected",
                permissions = new { contents = "read" },
            });
    });

app.MapPost(
    "/app/installations/42/access_tokens",
    (HttpRequest request, GitHubStubState state) =>
    {
        state.Record(request);
        return Results.Json(
            new { token = "e2e-installation-token", expires_at = "2099-01-01T00:00:00Z" },
            statusCode: StatusCodes.Status201Created);
    });

app.MapGet(
    "/user/installations/42/repositories",
    (HttpRequest request, GitHubStubState state) =>
    {
        state.Record(request);
        return Results.Ok(new { total_count = 1, repositories = Array.Empty<object>() });
    });

app.MapGet(
    "/installation/repositories",
    (HttpRequest request, GitHubStubState state) =>
    {
        state.Record(request);
        return Results.Ok(
            new
            {
                total_count = 2,
                repositories = new[]
                {
                    state.Repository(BaseUrl(request)), GitOpsMixedStubEndpoints.ConsoleRepository(BaseUrl(request)),
                },
            });
    });

app.MapGet(
    "/repositories/9001",
    (HttpRequest request, GitHubStubState state) =>
    {
        state.Record(request);
        return Results.Ok(state.Repository(BaseUrl(request)));
    });

app.MapGet(
    "/repos/pole-e2e/source-build-sample/branches",
    (HttpRequest request, GitHubStubState state) =>
    {
        state.Record(request);
        return Results.Ok(new[] { state.Branch(state.CurrentRevision) });
    });

app.MapGet(
    "/repos/pole-e2e/source-build-sample/branches/{branch}",
    (
        string branch, HttpRequest request,
        GitHubStubState state) =>
    {
        state.Record(request);
        return branch is "main" or "gitops-application"
            ? Results.Ok(
                new
                {
                    name = branch,
                    commit = new { sha = state.CurrentRevision.CommitSha },
                    @protected = true,
                })
            : Results.NotFound();
    });

app.MapGet(
    "/repos/pole-e2e/source-build-sample/git/commits/{commitSha}",
    (
        string commitSha, HttpRequest request,
        GitHubStubState state) =>
    {
        state.Record(request);
        return state.TryGetRevisionByCommit(commitSha, out var revision)
            ? Results.Ok(new { tree = new { sha = revision.TreeSha } })
            : Results.NotFound();
    });

app.MapGet(
    "/repos/pole-e2e/source-build-sample/git/trees/{treeSha}",
    (
        string treeSha, HttpRequest request,
        GitHubStubState state) =>
    {
        state.Record(request);
        return !state.TryGetRevisionByTree(treeSha, out _)
            ? Results.NotFound()
            : Results.Ok(
                new
                {
                    tree = new[]
                    {
                        new { path = "Dockerfile", type = "blob" },
                        new { path = "src/Pole.E2E.Sample.csproj", type = "blob" },
                        new { path = "src/Program.cs", type = "blob" },
                    },
                    truncated = false,
                });
    });

app.MapGet(
    "/repos/pole-e2e/source-build-sample/contents/src/Pole.E2E.Sample.csproj",
    (HttpRequest request, GitHubStubState state) =>
    {
        state.Record(request);
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(state.CurrentProject));
        return Results.Ok(new { encoding = "base64", content });
    });

app.MapGet(
    "/repos/pole-e2e/source-build-sample/contents/Dockerfile",
    (HttpRequest request, GitHubStubState state) =>
    {
        state.Record(request);
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(state.CurrentDockerfile));
        return Results.Ok(new { encoding = "base64", content });
    });

app.MapGet(
    "/repos/pole-e2e/source-build-sample/tarball/{commitSha}",
    (
        string commitSha, HttpRequest request,
        GitHubStubState state) =>
    {
        state.Record(request);
        return state.TryGetRevisionByCommit(commitSha, out var revision)
            ? Results.File(state.CreateArchive(revision), "application/gzip")
            : Results.NotFound();
    });

app.MapGitOpsStub(IsAuthorized);
app.MapGitOpsMixedStub(IsAuthorized);

app.Run();

static bool IsAuthorized(HttpRequest request, IConfiguration configuration) => string.Equals(
    request.Headers["X-Pole-E2E-Stub-Key"],
    configuration["GitHubStub:ControlKey"],
    StringComparison.Ordinal);

static string BaseUrl(HttpRequest request) => $"{request.Scheme}://{request.Host}";

namespace Pole.Tests.E2E.GitHubStub
{
    public sealed record RevisionRequest(
        string Revision
    );
}
