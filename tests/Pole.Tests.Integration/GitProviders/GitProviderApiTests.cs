using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitProviders.Application.Common;
using Pole.Features.GitProviders.Application.GitHub;
using Pole.Features.GitProviders.Application.Migrations;
using Pole.Features.GitProviders.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitProviders;

public sealed class GitProviderApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task DerivedSetupStateMemberFilters_AreTranslatedAndMatchTheExpectedSubtype()
    {
        const string sharedStateHash = "shared-state-hash";
        var now = DateTime.UtcNow;
        var installAttempt = GitHubSetupAttemptFactory.Create(
            TestData.TenantId,
            TestData.UserId,
            sharedStateHash,
            new GitHubSetupAttemptFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                now,
                now.AddMinutes(10)));
        var oauthAttempt = GitHubSetupAttemptFactory.Create(
            TestData.TenantId,
            TestData.UserId,
            "superseded-install-state-hash",
            new GitHubSetupAttemptFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                now,
                now.AddMinutes(10))) with
        {
            State = new AwaitingGitHubOAuth(
                sharedStateHash,
                "protected-code-verifier",
                new GitHubInstallationMetadata(
                    "42",
                    "account-1",
                    "lsbV",
                    GitProviderAccountType.User,
                    GitProviderRepositorySelection.Selected))
        };
        var otherOAuthAttempt = oauthAttempt with
        {
            Id = ObjectId.GenerateNewId().ToString(),
            State = ((AwaitingGitHubOAuth)oauthAttempt.State) with
            {
                OAuthStateHash = "other-state-hash"
            }
        };

        await Fixture.DbContext.GitHubSetupAttempts.InsertManyAsync(
            [installAttempt, oauthAttempt, otherOAuthAttempt]);

        var installFilter = Builders<GitHubSetupAttempt>.Filter.And(
            Builders<GitHubSetupAttempt>.Filter.Eq(
                attempt => ((AwaitingGitHubInstallation)attempt.State).InstallStateHash,
                sharedStateHash),
            Builders<GitHubSetupAttempt>.Filter.OfType<GitHubSetupState, AwaitingGitHubInstallation>(
                attempt => attempt.State));
        var oauthFilter = Builders<GitHubSetupAttempt>.Filter.And(
            Builders<GitHubSetupAttempt>.Filter.Eq(
                attempt => ((AwaitingGitHubOAuth)attempt.State).OAuthStateHash,
                sharedStateHash),
            Builders<GitHubSetupAttempt>.Filter.OfType<GitHubSetupState, AwaitingGitHubOAuth>(
                attempt => attempt.State));

        var matchingInstallAttempt = await Fixture.DbContext.GitHubSetupAttempts
            .Find(installFilter)
            .SingleAsync();
        var matchingOAuthAttempt = await Fixture.DbContext.GitHubSetupAttempts
            .Find(oauthFilter)
            .SingleAsync();

        Assert.Equal(installAttempt.Id, matchingInstallAttempt.Id);
        Assert.Equal(oauthAttempt.Id, matchingOAuthAttempt.Id);
    }

    [Fact]
    public async Task Migration_CreatesGlobalInstallationUniqueSourceLinkAndSetupTtlIndexes()
    {
        await new GitProviderConnectionsMigration().Up(Fixture.Database);
        await new GitHubSetupStateMigration().Up(Fixture.Database);

        var connectionIndexes = await Fixture.Database
            .GetCollection<BsonDocument>("GitProviderConnections")
            .Indexes.ListAsync();
        var connectionIndex = (await connectionIndexes.ToListAsync())
            .Single(index => index["name"] == "UX_GitProviderConnections_Provider_Installation");
        Assert.True(connectionIndex["unique"].AsBoolean);

        var sourceIndexes = await Fixture.Database
            .GetCollection<BsonDocument>("SourceRepositories")
            .Indexes.ListAsync();
        var sourceIndex = (await sourceIndexes.ToListAsync())
            .Single(index => index["name"] == "UX_SourceRepositories_Tenant_Repository_Branch_Candidate");
        Assert.True(sourceIndex["unique"].AsBoolean);

        var setupIndexes = await Fixture.Database
            .GetCollection<BsonDocument>("GitHubSetupAttempts")
            .Indexes.ListAsync();
        var setupIndexDocuments = await setupIndexes.ToListAsync();
        var installStateIndex = setupIndexDocuments.Single(index => index["name"] == "UX_GitHubSetupAttempts_InstallState");
        var oauthStateIndex = setupIndexDocuments.Single(index => index["name"] == "UX_GitHubSetupAttempts_OAuthState");
        var ttlIndex = setupIndexDocuments.Single(index => index["name"] == "TTL_GitHubSetupAttempts_ExpiresAtUtc");
        Assert.True(installStateIndex["unique"].AsBoolean);
        Assert.True(installStateIndex["sparse"].AsBoolean);
        Assert.Equal("State.InstallStateHash", installStateIndex["key"].AsBsonDocument.Names.Single());
        Assert.True(oauthStateIndex["unique"].AsBoolean);
        Assert.True(oauthStateIndex["sparse"].AsBoolean);
        Assert.Equal("State.OAuthStateHash", oauthStateIndex["key"].AsBsonDocument.Names.Single());
        Assert.Equal(0, ttlIndex["expireAfterSeconds"].ToInt32());
    }

    [Fact]
    public async Task GitHubSetup_UsesPkceConsumesOneTimeStateAndPersistsConnectionMetadata()
    {
        var setup = await BeginOAuthAsync();
        var pendingAttempt = await Fixture.DbContext.GitHubSetupAttempts.Find(_ => true).SingleAsync();
        var pendingOAuth = Assert.IsType<AwaitingGitHubOAuth>(pendingAttempt.State);
        Assert.NotEmpty(pendingOAuth.ProtectedCodeVerifier);
        Assert.Equal("42", pendingOAuth.Installation.ExternalInstallationId);

        var setupReplay = await Client.GetAsync(SetupCallbackUrl(setup.InstallState));
        AssertRedirect(setupReplay, "github", "error");
        AssertRedirect(setupReplay, "reason", "expired-or-invalid-state");

        var callback = await Client.GetAsync(OAuthCallbackUrl(setup.OAuthState));
        AssertRedirect(callback, "github", "connected");
        var connectionId = RedirectQuery(callback)["connectionId"].Single();
        Assert.False(string.IsNullOrWhiteSpace(connectionId));

        Assert.NotNull(GitHubClient.LastCodeVerifier);
        Assert.Equal(
            setup.CodeChallenge,
            new GitHubSetupTokenService().CreateCodeChallenge(GitHubClient.LastCodeVerifier));
        Assert.Equal("discarded-user-access-token", GitHubClient.LastUserAccessToken);

        var oauthReplay = await Client.GetAsync(OAuthCallbackUrl(setup.OAuthState));
        AssertRedirect(oauthReplay, "reason", "expired-or-invalid-state");

        var attempt = await Fixture.DbContext.GitHubSetupAttempts.Find(_ => true).SingleAsync();
        Assert.IsType<ConsumedGitHubSetup>(attempt.State);
        var attemptDocument = await Fixture.Database
            .GetCollection<BsonDocument>("GitHubSetupAttempts")
            .Find(Builders<BsonDocument>.Filter.Empty)
            .SingleAsync();
        var persistedState = attemptDocument["State"].AsBsonDocument;
        Assert.False(persistedState.Contains("InstallStateHash"));
        Assert.False(persistedState.Contains("OAuthStateHash"));
        Assert.False(persistedState.Contains("ProtectedCodeVerifier"));

        var connection = await Fixture.DbContext.GitProviderConnections.Find(_ => true).SingleAsync();
        Assert.Equal(connectionId, connection.Id);
        Assert.Equal(TestData.TenantId, connection.TenantId);
        Assert.Equal("42", connection.ExternalInstallationId);
        Assert.Equal("lsbV", connection.AccountLogin);
    }

    [Fact]
    public async Task GitHubSetup_RejectsExpiredStateAndSpendsOAuthStateWhenOwnershipFails()
    {
        var install = await BeginInstallationAsync();
        await Fixture.DbContext.GitHubSetupAttempts.UpdateOneAsync(
            _ => true,
            Builders<GitHubSetupAttempt>.Update.Set(
                attempt => attempt.ExpiresAtUtc,
                DateTime.UtcNow.AddMinutes(-1)));

        var expired = await Client.GetAsync(SetupCallbackUrl(install.State));
        AssertRedirect(expired, "reason", "expired-or-invalid-state");

        await Fixture.DbContext.GitHubSetupAttempts.DeleteManyAsync(_ => true);
        var oauth = await BeginOAuthAsync();
        GitHubClient.CanAccessInstallation = false;

        var denied = await Client.GetAsync(OAuthCallbackUrl(oauth.OAuthState));
        AssertRedirect(denied, "reason", "installation-access-denied");
        var replay = await Client.GetAsync(OAuthCallbackUrl(oauth.OAuthState));
        AssertRedirect(replay, "reason", "expired-or-invalid-state");
        Assert.False(await Fixture.DbContext.GitProviderConnections.Find(_ => true).AnyAsync());
    }

    [Fact]
    public async Task Install_RequiresTenantMemberOrHigher()
    {
        await SetCurrentUserRoleAsync(TenantRole.Viewer);

        var forbidden = await Client.PostAsync("/api/v1/git-provider-connections/github/install", null);

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        await SetCurrentUserRoleAsync(TenantRole.Member);
        var allowed = await Client.PostAsync("/api/v1/git-provider-connections/github/install", null);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Setup_IsIdempotentForTheSameTenant()
    {
        var existing = CreateConnection(TestData.TenantId, "42", accountLogin: "old-login");
        await Fixture.DbContext.GitProviderConnections.InsertOneAsync(existing);
        var setup = await BeginOAuthAsync();

        var callback = await Client.GetAsync(OAuthCallbackUrl(setup.OAuthState));

        AssertRedirect(callback, "connectionId", existing.Id);
        var connections = await Fixture.DbContext.GitProviderConnections.Find(_ => true).ToListAsync();
        var updated = Assert.Single(connections);
        Assert.Equal(existing.Id, updated.Id);
        Assert.Equal("lsbV", updated.AccountLogin);
    }

    [Fact]
    public async Task Setup_RejectsInstallationAlreadyBoundToAnotherTenant()
    {
        await Fixture.DbContext.GitProviderConnections.InsertOneAsync(
            CreateConnection("507f1f77bcf86cd799439099", "42"));
        var setup = await BeginOAuthAsync();

        var callback = await Client.GetAsync(OAuthCallbackUrl(setup.OAuthState));

        AssertRedirect(callback, "reason", "installation-already-linked");
        Assert.Equal(1, await Fixture.DbContext.GitProviderConnections.CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task RepositoryWorkflow_ListsAnalyzesLinksReanalyzesAndCleansUp()
    {
        var connection = CreateConnection(TestData.TenantId, "42");
        await Fixture.DbContext.GitProviderConnections.InsertOneAsync(connection);

        var repositoryResponse = await Client.GetAsync(
            $"/api/v1/git-provider-connections/{connection.Id}/repositories?page=1&pageSize=25");
        var repositories = await repositoryResponse.Content.ReadFromJsonAsync<ProviderPage<ProviderRepositoryDto>>();
        Assert.Equal(HttpStatusCode.OK, repositoryResponse.StatusCode);
        Assert.NotNull(repositories);
        Assert.Equal("lsbV/pole-dotnet-sample", Assert.Single(repositories.Items).FullName);

        var branchResponse = await Client.GetAsync(
            $"/api/v1/git-provider-connections/{connection.Id}/repositories/{GitHubClient.Repository.Id}/branches");
        var branches = await branchResponse.Content.ReadFromJsonAsync<ProviderPage<ProviderBranchDto>>();
        Assert.Equal(HttpStatusCode.OK, branchResponse.StatusCode);
        Assert.Equal("main", Assert.Single(branches!.Items).Name);

        var analysisResponse = await Client.PostAsJsonAsync(
            $"/api/v1/git-provider-connections/{connection.Id}/repositories/{GitHubClient.Repository.Id}/analysis",
            new AnalyzeRepositoryRequestBody("main"));
        var analysis = await analysisResponse.Content.ReadFromJsonAsync<RepositoryAnalysisDto>();
        Assert.Equal(HttpStatusCode.OK, analysisResponse.StatusCode);
        Assert.NotNull(analysis);
        var candidate = Assert.Single(analysis.Candidates);
        Assert.Equal(SourceBuildPlanKind.DotNet.ToString(), candidate.Kind);

        var linkResponse = await Client.PostAsJsonAsync(
            $"/api/v1/git-provider-connections/{connection.Id}/repositories/{GitHubClient.Repository.Id}/links",
            new LinkSourceRepositoryRequestBody("main", candidate.CandidateKey));
        var linked = await linkResponse.Content.ReadFromJsonAsync<SourceRepositoryDto>();
        Assert.Equal(HttpStatusCode.Created, linkResponse.StatusCode);
        Assert.NotNull(linked);
        Assert.Equal(GitHubClient.Snapshot.CommitSha, linked.CommitSha);

        var disconnectConflict = await Client.DeleteAsync($"/api/v1/git-provider-connections/{connection.Id}");
        Assert.Equal(HttpStatusCode.Conflict, disconnectConflict.StatusCode);

        GitHubClient.Snapshot = GitHubClient.Snapshot with { CommitSha = "def9876543210" };
        var reanalyzeResponse = await Client.PostAsync(
            $"/api/v1/source-repositories/{linked.Id}/reanalyze",
            null);
        var reanalyzed = await reanalyzeResponse.Content.ReadFromJsonAsync<SourceRepositoryDto>();
        Assert.Equal(HttpStatusCode.OK, reanalyzeResponse.StatusCode);
        Assert.Equal("def9876543210", reanalyzed!.CommitSha);

        var listResponse = await Client.GetFromJsonAsync<SourceRepositoryDto[]>("/api/v1/source-repositories");
        Assert.Equal(linked.Id, Assert.Single(listResponse!).Id);

        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/v1/source-repositories/{linked.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Client.DeleteAsync($"/api/v1/git-provider-connections/{connection.Id}")).StatusCode);
    }

    [Fact]
    public async Task RepositoryEndpoints_DoNotCrossTenantBoundaries()
    {
        var connection = CreateConnection("507f1f77bcf86cd799439099", "42");
        await Fixture.DbContext.GitProviderConnections.InsertOneAsync(connection);

        var response = await Client.GetAsync($"/api/v1/git-provider-connections/{connection.Id}/repositories");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, GitHubClient.RepositoryCalls);
    }

    [Fact]
    public async Task RepositoryPagination_IsValidatedBeforeCallingProvider()
    {
        var connection = CreateConnection(TestData.TenantId, "42");
        await Fixture.DbContext.GitProviderConnections.InsertOneAsync(connection);

        var response = await Client.GetAsync(
            $"/api/v1/git-provider-connections/{connection.Id}/repositories?page=0&pageSize=101");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, GitHubClient.RepositoryCalls);
    }

    [Fact]
    public async Task RepositoryProviderFailure_IsMappedAtServiceBoundary()
    {
        var connection = CreateConnection(TestData.TenantId, "42");
        await Fixture.DbContext.GitProviderConnections.InsertOneAsync(connection);
        GitHubClient.RepositoryFailure = new GitHubApiException(
            "GitHub rate limit reached.",
            HttpStatusCode.TooManyRequests);

        var response = await Client.GetAsync(
            $"/api/v1/git-provider-connections/{connection.Id}/repositories");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    private async Task<InstallationStart> BeginInstallationAsync()
    {
        var response = await Client.PostAsync("/api/v1/git-provider-connections/github/install", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<BeginGitHubInstallationResponse>();
        Assert.NotNull(payload);
        return new InstallationStart(QueryValue(Query(payload.InstallUrl), "state"));
    }

    private async Task<OAuthStart> BeginOAuthAsync()
    {
        var installation = await BeginInstallationAsync();
        var response = await Client.GetAsync(SetupCallbackUrl(installation.State));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = RedirectQuery(response);
        return new OAuthStart(
            installation.State,
            QueryValue(query, "state"),
            QueryValue(query, "code_challenge"));
    }

    private static string SetupCallbackUrl(string state) =>
        $"/api/v1/integrations/github/setup-complete?installation_id=42&setup_action=install&state={Uri.EscapeDataString(state)}";

    private static string OAuthCallbackUrl(string state) =>
        $"/api/v1/integrations/github/oauth-callback?code=test-code&state={Uri.EscapeDataString(state)}";

    private static Dictionary<string, StringValues> RedirectQuery(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.NotNull(response.Headers.Location);
        return Query(response.Headers.Location.ToString());
    }

    private static Dictionary<string, StringValues> Query(string url) =>
        QueryHelpers.ParseQuery(new Uri(url, UriKind.RelativeOrAbsolute).IsAbsoluteUri
            ? new Uri(url).Query
            : new Uri(new Uri("https://pole.test"), url).Query);

    private static void AssertRedirect(HttpResponseMessage response, string key, string expected) =>
        Assert.Equal(expected, QueryValue(RedirectQuery(response), key));

    private static string QueryValue(Dictionary<string, StringValues> query, string key) =>
        query[key].Single() ?? throw new InvalidOperationException($"Query value '{key}' is missing.");

    private static GitProviderConnection CreateConnection(
        string tenantId,
        string installationId,
        string accountLogin = "lsbV") =>
        GitProviderConnectionFactory.Create(
            tenantId,
            GitProviderKind.GitHub,
            installationId,
            "1001",
            accountLogin,
            GitProviderAccountType.User,
            GitProviderRepositorySelection.Selected,
            TestData.UserId,
            new GitProviderConnectionFactory.CreateOptions(
                ObjectId.GenerateNewId().ToString(),
                DateTime.UtcNow,
                DateTime.UtcNow));

    private async Task SetCurrentUserRoleAsync(TenantRole role)
    {
        var tenant = await Fixture.DbContext.Tenants.Find(item => item.Id == TestData.TenantId).SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(
            item => item.Id == tenant.Id,
            tenant with
            {
                Members = tenant.Members
                    .Select(member => member.UserId == TestData.UserId ? member with { Role = role } : member)
                    .ToImmutableList()
            });
    }

    private sealed record InstallationStart(string State);
    private sealed record OAuthStart(string InstallState, string OAuthState, string CodeChallenge);
}
