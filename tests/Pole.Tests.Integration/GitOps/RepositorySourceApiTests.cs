using Pole.Features.GitOps.Compiler;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Migrations;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Application.Endpoints.Sources.Previews;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Lifecycle;
using Pole.Features.GitOps.Application.Sources;
using Pole.Features.GitOps.Contracts;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Migrations;
using Pole.Features.Outbox.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Secrets.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Domain;
using Pole.Features.WebServices.Migrations;
using Pole.Tests.Integration.Http;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed partial class RepositorySourceApiTests(MongoReplicaSetFixture fixture) : ApiIntegrationTestBase(fixture)
{
    private const string Root = "/api/v1/gitops/sources";
    private readonly TestRepositorySnapshots repository = new();

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        NamedCatalogTestData.ConfigureIsolatedApi(services);
        services.AddSingleton<IRepositorySnapshotProvider>(repository);
    }

    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new GitOpsIndexesMigration().Up(Fixture.Database);
        await new WebServiceSchemaValidationMigration().Up(Fixture.Database);
        await new WebServiceDependencyReferencesSchemaMigration().Up(Fixture.Database);
        await new PreparedWebServiceSchemaMigration().Up(Fixture.Database);
        await new WebServiceReleaseHistoryMigration().Up(Fixture.Database);
        await new Pole.Features.ResourceGroups.Migrations.TechnicalIdentityMigration().Up(Fixture.Database);
        await new TechnicalIdentityMigration().Up(Fixture.Database);
        await new ComputeProfileHistoryMigration().Up(Fixture.Database);
        await new WebServiceDeclaredPlacementSchemaMigration().Up(Fixture.Database);
        await NamedCatalogTestData.SeedAsync(Fixture);
    }

    [Fact]
    public async Task Preview_and_confirmation_are_bounded_sanitized_and_idempotent()
    {
        var source = await Create();
        using var created = await Client.PostAsJsonAsync($"{Root}/{source.Id.ToUpperInvariant()}/previews",
            new { intent = "Detach", expectedRevision = source.Revision });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var preview = (await created.Content.ReadFromJsonAsync<LifecyclePreviewView>())!;
        Assert.Null(preview.Commit);
        Assert.Equal("Detach", preview.Intent);
        Assert.Equal(0, preview.Actions.Total);
        var path = $"{Root}/{source.Id}/previews/{preview.Id.ToUpperInvariant()}";
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync(path + "?pageSize=201")).StatusCode);
        using var approved = await Client.PostAsJsonAsync(path + "/confirm",
            new { expectedRevision = preview.Revision });
        Assert.Equal(HttpStatusCode.Accepted, approved.StatusCode);
        var run = (await approved.Content.ReadFromJsonAsync<ConfirmLifecyclePreviewResponse>())!;
        using var repeated = await Client.PostAsJsonAsync(path + "/confirm",
            new { expectedRevision = preview.Revision });
        Assert.Equal(run.RunId, (await repeated.Content.ReadFromJsonAsync<ConfirmLifecyclePreviewResponse>())!.RunId);
        var json = await (await Client.GetAsync(path)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("specJson", json);
        Assert.DoesNotContain("snapshot", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credentials", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Detach")]
    [InlineData("ResourceChanges")]
    public async Task Preview_rejects_client_authored_actions_and_confirmation_overrides(string intent)
    {
        var source = await Create();
        var path = $"{Root}/{source.Id}/previews";
        using var forged = await Client.PostAsJsonAsync(path,
            new { intent, expectedRevision = 1, actions = new[] { "delete-all" } });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        using var created = await Client.PostAsJsonAsync(path, new { intent = "Detach", expectedRevision = 1 });
        var preview = (await created.Content.ReadFromJsonAsync<LifecyclePreviewView>())!;
        using var overridden = await Client.PostAsJsonAsync(path + $"/{preview.Id}/confirm",
            new { expectedRevision = preview.Revision, allowedResourceIds = new[] { "unreviewed" } });
        Assert.Equal(HttpStatusCode.BadRequest, overridden.StatusCode);
        using var scope = ApiServices.CreateScope();
        var retained = await scope.ServiceProvider.GetRequiredService<GitLifecyclePreviews>()
            .LoadAsync(new(TestData.TenantId, source.Id), preview.Id, default);
        Assert.IsType<GitPreviewAwaitingConfirmation>(retained!.State);
    }

    [Theory]
    [InlineData("{\"intent\":\"Adoption\",\"expectedRevision\":1,\"resources\":[null]}", HttpStatusCode.BadRequest)]
    [InlineData("{\"intent\":\"Removal\",\"expectedRevision\":1,\"resources\":[null]}", HttpStatusCode.BadRequest)]
    public async Task Preview_rejects_null_selectors(string json, HttpStatusCode status)
    {
        var source = await Create();
        using var response = await Client.PostAsync($"{Root}/{source.Id}/previews",
            new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task Adoption_rejects_two_ids_for_the_same_desired_address()
    {
        var source = await Create();
        using var response = await Client.PostAsJsonAsync($"{Root}/{source.Id}/previews", new
        {
            intent = "Adoption",
            expectedRevision = 1,
            resources = new[]
            {
                new { kind = "ResourceGroup", name = "group", resourceId = ObjectId.GenerateNewId().ToString() },
                new { kind = "ResourceGroup", name = "group", resourceId = ObjectId.GenerateNewId().ToString() }
            }
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Preview_intent_may_follow_ordinary_json_fields()
    {
        var source = await Create();
        using var response = await Client.PostAsJsonAsync($"{Root}/{source.Id}/previews",
            new { expectedRevision = 1, intent = "Detach" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var preview = (await response.Content.ReadFromJsonAsync<LifecyclePreviewView>())!;
        Assert.Equal("Detach", preview.Intent);
    }

    [Fact]
    public async Task ObjectIdsAreCanonicalAcrossCreateReplayReadsAndCommands()
    {
        repository.Publish("repo", DeclarativePipelineTests.Snapshot());
        var body = Body();
        Client.DefaultRequestHeaders.Remove("X-Pole-Tenant-ID");
        Client.DefaultRequestHeaders.Add("X-Pole-Tenant-ID", TestData.TenantId.ToUpperInvariant());
        using var created = await Client.PostAsJsonAsync(Root + "/",
            body with { ConnectionId = body.ConnectionId.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var source = (await created.Content.ReadFromJsonAsync<RepositorySourceView>())!;
        Assert.Equal(body.ConnectionId, source.Repository.ConnectionId);
        using var replay = await Client.PostAsJsonAsync(Root + "/", body);
        Assert.Equal(source.Id, (await replay.Content.ReadFromJsonAsync<RepositorySourceView>())!.Id);
        await Run(source.Id);
        var read = (await Client.GetFromJsonAsync<RepositorySourceView>($"{Root}/{source.Id.ToUpperInvariant()}"))!;
        Assert.Equal(RepositoryRunStatus.Blocked, read.Status);
        var run = read.Synchronization.LatestRunId!.ToUpperInvariant();
        using var details = await Client.GetAsync($"{Root}/{source.Id.ToUpperInvariant()}/runs/{run}");
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        using var retry = await Client.PostAsJsonAsync($"{Root}/{source.Id.ToUpperInvariant()}/retry",
            new RetryRepositorySourceBody(1, Guid.NewGuid(), run));
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
    }

    [Fact]
    public async Task SourceDetailIncludesSanitizedLatestRunAndBlockers()
    {
        var source = await Create();
        await Run(source.Id);
        var current = (await Read(source.Id))!;
        Assert.Equal(RepositoryRunStatus.Blocked, current.Status);
        Assert.Equal(current.Synchronization.LatestRunId, current.LatestRun!.Id);
        Assert.NotEmpty(current.LatestRun.Diagnostics);
        using var response = await Client.GetAsync($"{Root}/{source.Id}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("specJson", body);
        Assert.DoesNotContain("acceptedSnapshot", body);
    }

    private CreateRepositorySourceBody Body(Guid? key = null) => new(key ?? Guid.NewGuid(),
        repository.Provider,
        TestRepositorySnapshots.ConnectionId,
        "repo",
        "main",
        "");

    private async Task<RepositorySourceView> Create(Guid? key = null, ManifestFile[]? files = null)
    {
        repository.Publish("repo", files ?? DeclarativePipelineTests.Snapshot());
        using var response = await Client.PostAsJsonAsync(Root + "/", Body(key));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<RepositorySourceView>())!;
    }

    private async Task Run(string source)
    {
        using var scope = ApiServices.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RepositorySourceJob>()
            .ProcessAsync(new(TestData.TenantId, source), default);
    }

    private async Task<HttpResponseMessage> Sync(string source, ManifestFile[]? files = null)
    {
        if (files is not null)
            repository.Publish("repo", files);
        var response =
            await Client.PostAsJsonAsync($"{Root}/{source}/sync", new SyncRepositorySourceBody(1, Guid.NewGuid()));
        if (response.StatusCode == HttpStatusCode.Accepted)
            await Run(source);
        return response;
    }

    private async Task<HttpResponseMessage> Retry(string source)
    {
        var current = (await Read(source))!;
        var response = await Client.PostAsJsonAsync($"{Root}/{source}/retry",
            new RetryRepositorySourceBody(current.Revision, Guid.NewGuid(), current.Synchronization.LatestRunId!));
        if (response.StatusCode == HttpStatusCode.Accepted)
            await Run(source);
        return response;
    }

    private Task<RepositorySourceView?> Read(string source) =>
        Client.GetFromJsonAsync<RepositorySourceView>($"{Root}/{source}");

    private static async Task<JsonElement> AssertProblem(HttpResponseMessage response,
        HttpStatusCode status,
        string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal($"tag:pole.dev,2026:problem:{code}", body.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        return body;
    }


    [Fact]
    public async Task Create_worker_ResourceGroup_and_status_form_one_durable_slice()
    {
        var source = await Create(files:
        [
            new("group.yaml",
                "apiVersion: pole.io/v1alpha1\nkind: ResourceGroup\nmetadata: {name: demo}\nspec: {}")
        ]);
        Assert.Equal(RepositoryRunStatus.Queued, source.Status);
        Assert.Equal(1, source.Synchronization.RequestedGeneration);
        Assert.Null(source.Synchronization.CurrentRunId);
        Assert.Equal(1,
            await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(item =>
                item.EventType == "pole.gitops/v1/source-work-requested"));
        await Run(source.Id);
        var applied = (await Read(source.Id))!;
        Assert.Equal(RepositoryRunStatus.ConfigurationApplied, applied.Status);
        Assert.Equal(1, applied.Synchronization.CompletedGeneration);
        Assert.Equal(applied.Synchronization.ObservedCommit, applied.Synchronization.FullyAppliedCommit);
        Assert.Equal(1, applied.Inventory.Owned);
        var operations = await Client.GetFromJsonAsync<RepositoryOperationPage>(
            $"{Root}/{source.Id}/runs/{applied.Synchronization.LatestRunId}/operations");
        Assert.Equal(RepositoryOperationStatus.Succeeded, Assert.Single(operations!.Items).Status);
        await Run(source.Id);
        Assert.Equal(1, await Fixture.DbContext.ResourceGroups.CountDocumentsAsync(_ => true));
        Assert.Equal(1,
            await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(item =>
                item.EventType == "pole.resource-groups/v1/resource-group-created"));
    }

    [Fact]
    public async Task Concurrent_creation_is_idempotent_and_different_input_conflicts()
    {
        var key = Guid.NewGuid();
        var sources = await Task.WhenAll(Create(key), Create(key));
        Assert.Equal(sources[0].Id, sources[1].Id);
        using var conflict = await Client.PostAsJsonAsync(Root + "/", Body(key) with { Branch = "other" });
        await AssertProblem(conflict, HttpStatusCode.Conflict, "gitops.idempotency-conflict");
        Assert.Equal(1,
            await Fixture.Database.GetCollection<RepositorySource>("GitRepositorySources")
                .CountDocumentsAsync(_ => true));
    }

    [Theory]
    [InlineData("get")]
    [InlineData("sync")]
    [InlineData("retry")]
    [InlineData("runs")]
    [InlineData("operations")]
    public async Task Missing_source_and_run_use_standard_tenant_scoped_not_found(string operation)
    {
        var source = ObjectId.GenerateNewId().ToString();
        var run = ObjectId.GenerateNewId().ToString();
        using var response = operation switch
        {
            "get" => await Client.GetAsync($"{Root}/{source}"),
            "sync" => await Client.PostAsJsonAsync($"{Root}/{source}/sync",
                new SyncRepositorySourceBody(1, Guid.NewGuid())),
            "retry" => await Client.PostAsJsonAsync($"{Root}/{source}/retry",
                new RetryRepositorySourceBody(1, Guid.NewGuid(), run)),
            "runs" => await Client.GetAsync($"{Root}/{source}/runs"),
            _ => await Client.GetAsync($"{Root}/{source}/runs/{run}/operations")
        };
        await AssertProblem(response, HttpStatusCode.NotFound, "gitops.source-not-found");
    }

    [Fact]
    public async Task Policy_update_requires_revision_and_rejects_location_or_forged_authority()
    {
        var source = await Create();
        using var stale = await Client.PutAsJsonAsync($"{Root}/{source.Id}",
            new { expectedRevision = 99, pollIntervalSeconds = 600 });
        await AssertProblem(stale, HttpStatusCode.Conflict, "gitops.revision-conflict");
        using var forged = await Client.PutAsJsonAsync($"{Root}/{source.Id}",
            new
            {
                expectedRevision = 1,
                pollIntervalSeconds = 600,
                branch = "other",
                tenantId = "foreign",
                prune = "Prune"
            });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        using var updated = await Client.PutAsJsonAsync($"{Root}/{source.Id}",
            new { expectedRevision = 1, pollIntervalSeconds = 600 });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var view = (await Read(source.Id))!;
        Assert.Equal(2, view.Revision);
        Assert.Equal(source.Repository, view.Repository);
        Assert.Equal(600, view.PollIntervalSeconds);
    }

    [Fact]
    public async Task Pause_resume_and_prune_policy_are_server_owned_and_resume_reads_current_branch()
    {
        var files = new[] { new ManifestFile("group.yaml",
            "apiVersion: pole.io/v1alpha1\nkind: ResourceGroup\nmetadata: {name: demo}\nspec: {}") };
        var source = await Create(files: files);
        await Run(source.Id);
        using var pausedResponse = await Client.PostAsJsonAsync($"{Root}/{source.Id}/pause",
            new PauseRepositorySourceBody(source.Revision));
        Assert.Equal(HttpStatusCode.OK, pausedResponse.StatusCode);
        var paused = (await pausedResponse.Content.ReadFromJsonAsync<RepositorySourceView>())!;
        Assert.Equal(GitSourceActivity.Paused, paused.Activity);
        using var sync = await Client.PostAsJsonAsync($"{Root}/{source.Id}/sync",
            new SyncRepositorySourceBody(paused.Revision, Guid.NewGuid()));
        await AssertProblem(sync, HttpStatusCode.Conflict, "gitops.source-inactive");
        using var retry = await Client.PostAsJsonAsync($"{Root}/{source.Id}/retry",
            new RetryRepositorySourceBody(paused.Revision, Guid.NewGuid(), paused.Synchronization.LatestRunId!));
        await AssertProblem(retry, HttpStatusCode.Conflict, "gitops.source-inactive");
        using var policy = await Client.PutAsJsonAsync($"{Root}/{source.Id}",
            new UpdateRepositorySourceBody(paused.Revision, 600, GitPrunePolicy.Prune));
        Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
        var changed = (await policy.Content.ReadFromJsonAsync<RepositorySourceView>())!;
        Assert.Equal(GitPrunePolicy.Prune, changed.Prune);
        Assert.Equal(changed.Synchronization.RequestedGeneration, changed.Synchronization.CompletedGeneration);
        var commit = repository.Publish("repo", files.Select(file => file with
        { Content = file.Content.Replace("spec: {}", "spec: {description: changed}") }).ToArray());
        using var resumedResponse = await Client.PostAsJsonAsync($"{Root}/{source.Id}/resume",
            new ResumeRepositorySourceBody(changed.Revision));
        Assert.Equal(HttpStatusCode.OK, resumedResponse.StatusCode);
        await Run(source.Id);
        var applied = (await Read(source.Id))!;
        Assert.Equal(GitSourceActivity.Active, applied.Activity);
        Assert.Equal(commit, applied.Synchronization.FullyAppliedCommit);
    }

    [Fact]
    public async Task Delete_source_requires_detach_and_deleted_creation_key_cannot_resurrect_source()
    {
        var key = Guid.NewGuid();
        var source = await Create(key, files: []);
        using var pending = await Client.DeleteAsync($"{Root}/{source.Id}?expectedRevision=1");
        await AssertProblem(pending, HttpStatusCode.Conflict, "gitops.source-busy");
        using var pause = await Client.PostAsJsonAsync($"{Root}/{source.Id}/pause", new PauseRepositorySourceBody(1));
        var paused = (await pause.Content.ReadFromJsonAsync<RepositorySourceView>())!;
        using var deleted = await Client.DeleteAsync($"{Root}/{source.Id}?expectedRevision={paused.Revision}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var read = await Client.GetAsync($"{Root}/{source.Id}");
        await AssertProblem(read, HttpStatusCode.NotFound, "gitops.source-not-found");
        using var replay = await Client.PostAsJsonAsync(Root + "/", Body(key));
        await AssertProblem(replay, HttpStatusCode.Conflict, "gitops.source-deleted");
        Assert.Empty((await Client.GetFromJsonAsync<RepositorySourcePage>(Root + "/"))!.Items);

        var owned = await Create(files:
        [
            new("group.yaml", "apiVersion: pole.io/v1alpha1\nkind: ResourceGroup\nmetadata: {name: owned}\nspec: {}")
        ]);
        await Run(owned.Id);
        using var ownedDeletion = await Client.DeleteAsync($"{Root}/{owned.Id}?expectedRevision=1");
        await AssertProblem(ownedDeletion, HttpStatusCode.Conflict, "gitops.detach-required");
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("/root")]
    [InlineData("a//b")]
    [InlineData("a/../b")]
    public async Task Invalid_directory_is_rejected_before_source_creation(string directory)
    {
        using var response = await Client.PostAsJsonAsync(Root + "/", Body() with { ManifestDirectory = directory });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0,
            await Fixture.Database.GetCollection<RepositorySource>("GitRepositorySources")
                .CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Manual_snapshot_routes_are_removed_and_queries_are_bounded()
    {
        var source = await Create();
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.PutAsJsonAsync($"{Root}/{source.Id}/snapshot", new { files = new[] { "raw" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Client.PostAsJsonAsync($"{Root}/{source.Id}/resume", new { expectedFence = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync(Root + "/?pageSize=101")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Client.GetAsync($"{Root}/{source.Id}/runs?pageSize=101")).StatusCode);
    }

    [Fact]
    public async Task Retry_preserves_accepted_commit_while_Sync_reads_the_changed_branch()
    {
        var source = await Create();
        await Run(source.Id);
        var blocked = (await Read(source.Id))!;
        Assert.Equal(RepositoryRunStatus.Blocked, blocked.Status);
        var secret = await Fixture.Database.GetCollection<Secret>("Secrets").Find(_ => true).SingleAsync();
        using var supplied = await Client.PostAsJsonAsync($"/api/v1/secrets/{secret.Id}/versions",
            new
            {
                expectedVersion = 0,
                values = new Dictionary<string, string> { ["API_TOKEN"] = "synthetic-secret" }
            });
        Assert.Equal(HttpStatusCode.OK, supplied.StatusCode);
        var nextCommit = repository.Publish("repo", DeclarativePipelineTests.Snapshot("2"));
        var readCount = repository.ReadCommits.Count;
        using var retried = await Retry(source.Id);
        Assert.Equal(HttpStatusCode.Accepted, retried.StatusCode);
        var applied = (await Read(source.Id))!;
        Assert.Equal(RepositoryRunStatus.ConfigurationApplied, applied.Status);
        Assert.Equal(blocked.Synchronization.AttemptedCommit, applied.Synchronization.FullyAppliedCommit);
        Assert.Equal(readCount, repository.ReadCommits.Count);
        var run = await Client.GetFromJsonAsync<RepositoryRunView>(
            $"{Root}/{source.Id}/runs/{applied.Synchronization.LatestRunId}");
        Assert.Equal(blocked.Synchronization.LatestRunId, run!.RetryOfRunId);
        using var synced = await Sync(source.Id);
        Assert.Equal(HttpStatusCode.Accepted, synced.StatusCode);
        Assert.Equal(nextCommit, (await Read(source.Id))!.Synchronization.FullyAppliedCommit);
        var text = await Client.GetStringAsync($"{Root}/{source.Id}/runs/{run.Id}/operations");
        Assert.DoesNotContain("synthetic-secret", text);
        Assert.DoesNotContain("specJson", text);
    }

    [Fact]
    public async Task Invalid_snapshot_records_a_failed_run_without_acceptance_or_resource_writes()
    {
        var source = await Create(files: [new("invalid.yaml", "kind: [")]);
        await Run(source.Id);
        var failed = (await Read(source.Id))!;
        Assert.Equal(RepositoryRunStatus.Failed, failed.Status);
        Assert.Equal(1, failed.Synchronization.CompletedGeneration);
        Assert.NotNull(failed.Synchronization.ObservedCommit);
        Assert.Null(failed.Synchronization.AttemptedCommit);
        Assert.Equal(0, failed.Inventory.Owned);
        Assert.Equal(0, await Fixture.DbContext.ResourceGroups.CountDocumentsAsync(_ => true));
        var run = await Client.GetFromJsonAsync<RepositoryRunView>(
            $"{Root}/{source.Id}/runs/{failed.Synchronization.LatestRunId}");
        Assert.NotEmpty(run!.Diagnostics);
    }

    [Fact]
    public async Task Sources_runs_and_operations_enforce_authentication_roles_and_tenant_isolation()
    {
        var source = await Create();
        await Run(source.Id);
        var current = (await Read(source.Id))!;
        var run = current.Synchronization.LatestRunId!;
        Client.DefaultRequestHeaders.Add("X-Test-Unauthenticated", "true");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"{Root}/{source.Id}")).StatusCode);
        Client.DefaultRequestHeaders.Remove("X-Test-Unauthenticated");
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync($"{Root}/{source.Id}")).StatusCode);
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, TestData.TenantId);
        var tenant = await Fixture.DbContext.Tenants.Find(item => item.Id == TestData.TenantId).SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(item => item.Id == tenant.Id,
            tenant with
            {
                Members =
                [
                    TenantFactory.CreateMember(tenant.Id, TestData.UserId, TestData.UserEmail, TenantRole.Viewer)
                ]
            });
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"{Root}/{source.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Sync(source.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client.PostAsJsonAsync($"{Root}/{source.Id}/retry",
                new RetryRepositorySourceBody(1, Guid.NewGuid(), run))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.PostAsJsonAsync(Root + "/", Body())).StatusCode);
        var other = TenantFactory.Create("Other", ObjectId.GenerateNewId().ToString());
        other = other with
        {
            Members =
            [
                TenantFactory.CreateMember(other.Id,
                    TestData.UserId,
                    TestData.UserEmail,
                    TenantRole.Owner)
            ]
        };
        await Fixture.DbContext.Tenants.InsertOneAsync(other);
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, other.Id);
        foreach (var suffix in new[] { "", "/runs", $"/runs/{run}", $"/runs/{run}/operations" })
            Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"{Root}/{source.Id}{suffix}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Sync(source.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.PostAsJsonAsync($"{Root}/{source.Id}/retry",
                new RetryRepositorySourceBody(1, Guid.NewGuid(), run))).StatusCode);
    }

    [Fact]
    public async Task Ownership_conflicts_are_visible_on_the_requesting_run_without_claiming_inventory()
    {
        var owner = await Create();
        await Run(owner.Id);
        var requester = await Create();
        await Run(requester.Id);
        var current = (await Read(requester.Id))!;
        Assert.Equal(RepositoryRunStatus.Blocked, current.Status);
        Assert.Equal(0, current.Inventory.Owned);
        var operations = await Client.GetFromJsonAsync<RepositoryOperationPage>(
            $"{Root}/{requester.Id}/runs/{current.Synchronization.LatestRunId}/operations");
        Assert.Contains(operations!.Items, item => item.Kind == "WebService" && item.Blocker == "OwnershipConflict");
    }

    [Fact]
    public async Task Accepted_snapshot_with_escaped_content_resumes_without_reapplying_the_external_byte_limit()
    {
        var source = await Create();
        var files = DeclarativePipelineTests.Snapshot().Select(file => file with
        {
            Content = string.Join("\n---\n",
                file.Content.Split("\n---\n").Select(content =>
                {
                    var document = JsonNode.Parse(content)!;
                    if (document["kind"]!.GetValue<string>() == "WebService")
                        document["spec"]!["environment"] = new JsonObject
                        { ["LONG_CONFIG"] = new string('+', 200_000) };
                    return document.ToJsonString(new JsonSerializerOptions
                    { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                }))
        }).ToArray();
        Assert.True(files.Sum(file => Encoding.UTF8.GetByteCount(file.Content)) < 1024 * 1024);
        using var submitted = await Sync(source.Id, files);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        Assert.Equal(RepositoryRunStatus.Blocked, (await Read(source.Id))!.Status);
        var secret = await Fixture.Database.GetCollection<Secret>("Secrets").Find(Builders<Secret>.Filter.Empty)
            .SingleAsync();
        using var supplied = await Client.PostAsJsonAsync($"/api/v1/secrets/{secret.Id}/versions",
            new { expectedVersion = 0, values = new Dictionary<string, string> { ["API_TOKEN"] = "" } });
        Assert.Equal(HttpStatusCode.OK, supplied.StatusCode);
        using var resumed = await Retry(source.Id);
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        Assert.Equal(RepositoryRunStatus.ConfigurationApplied, (await Read(source.Id))!.Status);
    }

    [Fact]
    public async Task Newly_created_catalog_entries_are_immediately_resolvable_by_name_with_profile_revision_one()
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        using var targetResponse = await Client.PostAsJsonAsync("/api/v1/platform/deployment-targets/",
            CatalogTechnicalNameApiTests.Body("deployment-targets", "new-lab"));
        Assert.Equal(HttpStatusCode.Created, targetResponse.StatusCode);
        var target = await targetResponse.Content.ReadFromJsonAsync<JsonElement>();
        var targetId = target.GetProperty("id").GetString()!;
        AddEligibleDeploymentTarget(targetId, "apps.new-lab.test");
        // Normal target readiness is independent of naming. This test host fakes Kubernetes probes.
        using var attested = await Client.PutAsJsonAsync(
            $"/api/v1/platform/deployment-targets/{targetId}/network-policy",
            new { enforcement = "Supported", allowIsolationNotEnforced = false, revision = 1 });
        Assert.Equal(HttpStatusCode.OK, attested.StatusCode);
        using var profileResponse = await Client.PostAsJsonAsync("/api/v1/platform/compute-profiles/",
            CatalogTechnicalNameApiTests.Body("compute-profiles", "new-small", targetId));
        Assert.Equal(HttpStatusCode.Created, profileResponse.StatusCode);
        var profile = await profileResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, profile.GetProperty("revision").GetInt64());
        var source = await Create();
        var files = DeclarativePipelineTests.Snapshot().Select(file => file with
        {
            Content = file.Content.Replace("\"deploymentTarget\":\"lab\"", "\"deploymentTarget\":\"new-lab\"")
                .Replace("\"name\":\"small\"", "\"name\":\"new-small\"")
        }).ToArray();
        using var submitted = await Sync(source.Id, files);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var service = await Fixture.Database.GetCollection<WebService>("WebServices")
            .Find(Builders<WebService>.Filter.Empty).SingleAsync();
        Assert.Equal(targetId, service.DeclaredPlacement!.DeploymentTargetId);
        Assert.Equal(profile.GetProperty("id").GetString(), service.DeclaredPlacement.ComputeProfileId);
        var placement = await Fixture.DbContext.ResourceDeployments.Find(item => item.ResourceId == service.Id)
            .SingleAsync();
        Assert.Equal(1, placement.ResolvedPlacement.Compute.ComputeProfileRevision);
        var secret = await Fixture.Database.GetCollection<Secret>("Secrets").Find(Builders<Secret>.Filter.Empty)
            .SingleAsync();
        using var supplied = await Client.PostAsJsonAsync($"/api/v1/secrets/{secret.Id}/versions",
            new { expectedVersion = 0, values = new Dictionary<string, string> { ["API_TOKEN"] = "" } });
        Assert.Equal(HttpStatusCode.OK, supplied.StatusCode);
        using var resumed = await Retry(source.Id);
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        Assert.Equal(RepositoryRunStatus.ConfigurationApplied, (await Read(source.Id))!.Status);
    }

    [Theory]
    [InlineData("retry")]
    [InlineData("update")]
    public async Task Prepared_service_manual_deployment_mutations_return_conflict_without_writes(string operation)
    {
        var source = await Create();
        await Sync(source.Id);
        var services = Fixture.Database.GetCollection<WebService>("WebServices");
        var service = await services.Find(Builders<WebService>.Filter.Empty).SingleAsync();
        Assert.IsType<PreparedWebServiceLifecycle>(service.Lifecycle);
        var placement = await Fixture.DbContext.ResourceDeployments.Find(item => item.ResourceId == service.Id)
            .SingleAsync();
        var outboxCount =
            await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty);
        var path = $"/api/v1/web-services/{service.Id}/deployments/{placement.Id}";
        using var response = operation == "retry"
            ? await Client.PostAsJsonAsync(path + "/retry",
                new { expectedDeploymentRevision = placement.Revision, expectedOperation = "Apply" })
            : await Client.PutAsJsonAsync(path, new { fixedReplicas = 2 });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("web-services.update-unavailable", await response.Content.ReadAsStringAsync());
        var retained = await services.Find(item => item.Id == service.Id).SingleAsync();
        Assert.Equal(service.Revision, retained.Revision);
        Assert.IsType<PreparedWebServiceLifecycle>(retained.Lifecycle);
        var retainedPlacement =
            await Fixture.DbContext.ResourceDeployments.Find(item => item.Id == placement.Id).SingleAsync();
        Assert.Equal(placement.Revision, retainedPlacement.Revision);
        Assert.Equal(placement.DesiredGeneration, retainedPlacement.DesiredGeneration);
        Assert.IsType<PreparedResourceDeploymentState>(retainedPlacement.State);
        Assert.Equal(outboxCount,
            await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
        Assert.Equal(0,
            await Fixture.Database.GetCollection<WebServiceRelease>("WebServiceReleases")
                .CountDocumentsAsync(Builders<WebServiceRelease>.Filter.Empty));
    }

    [Fact]
    public async Task Manual_duplicate_names_return_conflict_and_roll_back_placement_and_outbox()
    {
        var source = await Create();
        await Sync(source.Id);
        using var duplicateGroup = await Client.PostAsJsonAsync("/api/v1/resource-groups/",
            new { name = "DEMO", description = "manual", tags = Array.Empty<string>() });
        await AssertProblem(duplicateGroup, HttpStatusCode.Conflict, "resource-groups.name-already-exists");
        var group = await Fixture.DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("other-group"));
        var before = await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty);
        var placementCount =
            await Fixture.DbContext.ResourceDeployments.CountDocumentsAsync(
                Builders<Pole.Features.ResourceDeployments.Domain.ResourceDeployment>.Filter.Empty);
        var duplicate = new CreateWebServiceRequestBody("DEMO-API",
            group.Id,
            TestData.ValidWebServiceSpec(),
            new(null, [new(TestData.DefaultDeploymentTargetId, 1, TestData.DefaultComputeProfileId)]),
            []);
        using var conflict = await Client.PostAsJsonAsync("/api/v1/web-services/", duplicate);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var problem = await conflict.Content.ReadAsStringAsync();
        Assert.True(problem.Contains("web-services.name-already-exists", StringComparison.Ordinal), problem);
        Assert.Equal(before,
            await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
        Assert.Equal(placementCount,
            await Fixture.DbContext.ResourceDeployments.CountDocumentsAsync(
                Builders<Pole.Features.ResourceDeployments.Domain.ResourceDeployment>.Filter.Empty));
    }

    [Fact]
    public async Task Concurrent_manual_group_creation_returns_one_created_and_one_typed_conflict()
    {
        var responses = await Task.WhenAll(Client.PostAsJsonAsync("/api/v1/resource-groups/",
                new { name = "race", description = "first", tags = Array.Empty<string>() }),
            Client.PostAsJsonAsync("/api/v1/resource-groups/",
                new { name = "RACE", description = "second", tags = Array.Empty<string>() }));
        Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Conflict);
        Assert.Single(await Fixture.DbContext.ResourceGroups
            .Find(Builders<Pole.Features.ResourceGroups.Domain.ResourceGroup>.Filter.Empty).ToListAsync());
        Assert.Equal(1,
            await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(item =>
                item.EventType == "pole.resource-groups/v1/resource-group-created"));
        foreach (var response in responses)
            response.Dispose();
    }
}
