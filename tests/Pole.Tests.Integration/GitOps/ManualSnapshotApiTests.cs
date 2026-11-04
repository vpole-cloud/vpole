using Pole.Features.GitOps.Domain;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Migrations;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.GitOps.Application.Jobs;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.Outbox.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.Features.Tenants.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Features.WebServices.Application.Migrations;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed class ManualSnapshotApiTests(MongoReplicaSetFixture fixture) : ApiIntegrationTestBase(fixture)
{
    protected override void ConfigureTestServices(IServiceCollection services) => NamedCatalogTestData.ConfigureIsolatedApi(services);
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new GitOpsIndexesMigration().Up(Fixture.Database);
        await new WebServiceSchemaValidationMigration().Up(Fixture.Database);
        await new WebServiceDependencyReferencesSchemaMigration().Up(Fixture.Database);
        await new PreparedWebServiceSchemaMigration().Up(Fixture.Database);
        await new WebServiceReleaseHistoryMigration().Up(Fixture.Database);
        await new Pole.Features.ResourceGroups.Application.Migrations.TechnicalIdentityMigration().Up(Fixture.Database);
        await new TechnicalIdentityMigration().Up(Fixture.Database);
        await new ComputeProfileHistoryMigration().Up(Fixture.Database);
        await new WebServiceDeclaredPlacementSchemaMigration().Up(Fixture.Database);
        await NamedCatalogTestData.SeedAsync(Fixture);
    }
    private const string Root = "/api/v1/gitops/sources";
    private async Task<SnapshotSourceStatus> Create(Guid? key = null)
    {
        var response = await Client.PostAsJsonAsync(Root + "/", new { idempotencyKey = key ?? Guid.NewGuid(), sourceUrl = "https://github.com/lsbV/example" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SnapshotSourceStatus>())!;
    }
    private Task<HttpResponseMessage> Submit(string id, long fence = 0) => Client.PutAsJsonAsync($"{Root}/{id}/snapshot", new
    { expectedFence = fence, commit = "manual-commit", files = DeclarativePipelineTests.Snapshot() });
    private Task<SnapshotSourceStatus?> Read(string id) => Client.GetFromJsonAsync<SnapshotSourceStatus>($"{Root}/{id}");

    private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
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
    public async Task Source_without_an_execution_reports_AwaitingSnapshot_as_a_JSON_string()
    {
        var source = await Create();
        using var response = await Client.GetAsync($"{Root}/{source.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("AwaitingSnapshot", body.GetProperty("status").GetString());
        Assert.Equal(SnapshotSourceState.AwaitingSnapshot,
            body.Deserialize<SnapshotSourceStatus>(JsonSerializerOptions.Web)!.Status);
        Assert.Equal(0, await Fixture.Database.GetCollection<GitSourceExecution>("GitSourceExecutions")
            .CountDocumentsAsync(item => item.SourceId == source.Id));
    }

    public static IEnumerable<object[]> StoredProgressCases() =>
    [
        [new ApplyingSnapshot(), SnapshotSourceState.Applying, "Applying"],
        [new BlockedSnapshot(), SnapshotSourceState.Blocked, "Blocked"],
        [new AppliedSnapshot(), SnapshotSourceState.ConfigurationApplied, "ConfigurationApplied"],
        [new UnsupportedSnapshotSchema(), SnapshotSourceState.UnsupportedStoredSchema, "UnsupportedStoredSchema"],
        [new StaleSnapshotExecution(), SnapshotSourceState.StaleExecution, "StaleExecution"]
    ];

    [Theory]
    [MemberData(nameof(StoredProgressCases))]
    public async Task Current_stored_progress_projects_to_a_typed_status_with_the_existing_JSON_value(
        SnapshotProgress progress,
        SnapshotSourceState expectedState,
        string expectedJson)
    {
        var source = await Create();
        using var submitted = await Submit(source.Id);
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var executions = Fixture.Database.GetCollection<GitSourceExecution>("GitSourceExecutions");
        await executions.UpdateOneAsync(item => item.SourceId == source.Id,
            Builders<GitSourceExecution>.Update.Set(item => item.Status, progress));

        using var response = await Client.GetAsync($"{Root}/{source.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(expectedJson, body.GetProperty("status").GetString());
        Assert.Equal(expectedState, body.Deserialize<SnapshotSourceStatus>(JsonSerializerOptions.Web)!.Status);
    }

    [Theory]
    [InlineData("get")]
    [InlineData("resume")]
    [InlineData("submit")]
    public async Task Missing_source_uses_the_standard_Pole_problem_contract(string endpoint)
    {
        var id = ObjectId.GenerateNewId().ToString();
        using var response = endpoint switch
        {
            "get" => await Client.GetAsync($"{Root}/{id}"),
            "resume" => await Client.PostAsJsonAsync($"{Root}/{id}/resume", new { expectedFence = 1 }),
            _ => await Submit(id)
        };
        await AssertProblem(response, HttpStatusCode.NotFound, "gitops.source-not-found");
    }

    [Fact]
    public async Task Accepted_snapshot_with_escaped_content_resumes_without_reapplying_the_external_byte_limit()
    {
        var source = await Create();
        var files = DeclarativePipelineTests.Snapshot().Select(file => file with
        {
            Content = string.Join("\n---\n", file.Content.Split("\n---\n").Select(content =>
            {
                var document = JsonNode.Parse(content)!;
                if (document["kind"]!.GetValue<string>() == "WebService")
                    document["spec"]!["environment"] = new JsonObject { ["LONG_CONFIG"] = new string('+', 200_000) };
                return document.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }))
        }).ToArray();
        Assert.True(files.Sum(file => Encoding.UTF8.GetByteCount(file.Content)) < 1024 * 1024);
        using var submitted = await Client.PutAsJsonAsync($"{Root}/{source.Id}/snapshot", new
        { expectedFence = 0, commit = "escaped-content", files });
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        Assert.Equal(SnapshotSourceState.Blocked, (await Read(source.Id))!.Status);
        var secret = await Fixture.Database.GetCollection<Secret>("Secrets").Find(Builders<Secret>.Filter.Empty).SingleAsync();
        using var supplied = await Client.PostAsJsonAsync($"/api/v1/secrets/{secret.Id}/versions",
            new { expectedVersion = 0, values = new Dictionary<string, string> { ["API_TOKEN"] = "" } });
        Assert.Equal(HttpStatusCode.OK, supplied.StatusCode);
        using var resumed = await Client.PostAsJsonAsync($"{Root}/{source.Id}/resume", new { expectedFence = 1 });
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        Assert.Equal(SnapshotSourceState.ConfigurationApplied, (await Read(source.Id))!.Status);
    }

    [Fact]
    public async Task Stale_snapshot_submission_uses_the_standard_Pole_problem_contract()
    {
        var source = await Create();
        using var accepted = await Submit(source.Id);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var conflict = await Submit(source.Id);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType!.MediaType);
        var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gitops.stale-execution", problem.GetProperty("code").GetString());
        Assert.Equal("tag:pole.dev,2026:problem:gitops.stale-execution", problem.GetProperty("type").GetString());
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
        using var attested = await Client.PutAsJsonAsync($"/api/v1/platform/deployment-targets/{targetId}/network-policy",
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
        using var submitted = await Client.PutAsJsonAsync($"{Root}/{source.Id}/snapshot", new
        { expectedFence = 0, commit = "new-catalog", files });
        Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
        var service = await Fixture.Database.GetCollection<WebService>("WebServices").Find(Builders<WebService>.Filter.Empty).SingleAsync();
        Assert.Equal(targetId, service.DeclaredPlacement!.DeploymentTargetId);
        Assert.Equal(profile.GetProperty("id").GetString(), service.DeclaredPlacement.ComputeProfileId);
        var placement = await Fixture.DbContext.ResourceDeployments.Find(item => item.ResourceId == service.Id).SingleAsync();
        Assert.Equal(1, placement.ResolvedPlacement.Compute.ComputeProfileRevision);
        var secret = await Fixture.Database.GetCollection<Secret>("Secrets").Find(Builders<Secret>.Filter.Empty).SingleAsync();
        using var supplied = await Client.PostAsJsonAsync($"/api/v1/secrets/{secret.Id}/versions",
            new { expectedVersion = 0, values = new Dictionary<string, string> { ["API_TOKEN"] = "" } });
        Assert.Equal(HttpStatusCode.OK, supplied.StatusCode);
        using var resumed = await Client.PostAsJsonAsync($"{Root}/{source.Id}/resume", new { expectedFence = 1 });
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        Assert.Equal(SnapshotSourceState.ConfigurationApplied, (await Read(source.Id))!.Status);
    }

    [Theory]
    [InlineData("retry")]
    [InlineData("update")]
    public async Task Prepared_service_manual_deployment_mutations_return_conflict_without_writes(string operation)
    {
        var source = await Create();
        await Submit(source.Id);
        var services = Fixture.Database.GetCollection<WebService>("WebServices");
        var service = await services.Find(Builders<WebService>.Filter.Empty).SingleAsync();
        Assert.IsType<PreparedWebServiceLifecycle>(service.Lifecycle);
        var placement = await Fixture.DbContext.ResourceDeployments.Find(item => item.ResourceId == service.Id).SingleAsync();
        var outboxCount = await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty);
        var path = $"/api/v1/web-services/{service.Id}/deployments/{placement.Id}";
        using var response = operation == "retry"
            ? await Client.PostAsJsonAsync(path + "/retry", new { expectedDeploymentRevision = placement.Revision, expectedOperation = "Apply" })
            : await Client.PutAsJsonAsync(path, new { fixedReplicas = 2 });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("web-services.update-unavailable", await response.Content.ReadAsStringAsync());
        var retained = await services.Find(item => item.Id == service.Id).SingleAsync();
        Assert.Equal(service.Revision, retained.Revision);
        Assert.IsType<PreparedWebServiceLifecycle>(retained.Lifecycle);
        var retainedPlacement = await Fixture.DbContext.ResourceDeployments.Find(item => item.Id == placement.Id).SingleAsync();
        Assert.Equal(placement.Revision, retainedPlacement.Revision);
        Assert.Equal(placement.DesiredGeneration, retainedPlacement.DesiredGeneration);
        Assert.IsType<PreparedResourceDeploymentState>(retainedPlacement.State);
        Assert.Equal(outboxCount, await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
        Assert.Equal(0, await Fixture.Database.GetCollection<WebServiceRelease>("WebServiceReleases").CountDocumentsAsync(Builders<WebServiceRelease>.Filter.Empty));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Operator_can_submit_inspect_supply_values_and_resume_same_commit_after_event_or_restart(bool deliverEvent)
    {
        var key = Guid.NewGuid(); var source = await Create(key); Assert.Equal(source.Id, (await Create(key)).Id);
        using var response = await Submit(source.Id);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var blocked = (await response.Content.ReadFromJsonAsync<SnapshotSourceStatus>())!;
        Assert.Equal(SnapshotSourceState.Blocked, blocked.Status);
        Assert.Equal(1, blocked.Fence);
        Assert.Null(blocked.AppliedCommit);
        var service = await Fixture.Database.GetCollection<WebService>("WebServices").Find(Builders<WebService>.Filter.Empty).SingleAsync();
        Assert.IsType<PreparedWebServiceLifecycle>(service.Lifecycle);
        var operations = await Client.GetAsync($"/api/v1/resource-deployments/operations?resourceType=pole/web-service&resourceId={service.Id}");
        Assert.Equal(HttpStatusCode.OK, operations.StatusCode);
        Assert.Contains("Prepared", await operations.Content.ReadAsStringAsync());
        var secret = await Fixture.Database.GetCollection<Secret>("Secrets").Find(Builders<Secret>.Filter.Empty).SingleAsync();
        using var supplied = await Client.PostAsJsonAsync($"/api/v1/secrets/{secret.Id}/versions", new { expectedVersion = 0, values = new Dictionary<string, string> { ["API_TOKEN"] = "" } });
        Assert.Equal(HttpStatusCode.OK, supplied.StatusCode);
        using var restarted = TestServiceProviderFactory.Create(Fixture); using var scope = restarted.CreateScope();
        if (deliverEvent)
        {
            var messages = await Fixture.Database.GetCollection<OutboxMessage>("OutboxMessages").Find(item => item.EventType == "pole.secrets/v1/secret-changed").ToListAsync();
            var changed = messages.Select(item => JsonSerializer.Deserialize<SecretChangedEvent>(item.PayloadJson, EventExtensions.SerializerOptions)!)
                .Single(item => item.Change is SecretRotated);
            var consumer = scope.ServiceProvider.GetRequiredService<ResumeSnapshotOnSecretValueChanged>();
            await consumer.HandleAsync(changed with { EventCore = EventCore.Create(ObjectId.GenerateNewId().ToString()) });
            Assert.Equal(SnapshotSourceState.Blocked, (await Read(source.Id))!.Status);
            await consumer.HandleAsync(changed); await consumer.HandleAsync(changed); // Redelivery is harmless.
        }
        else
        {
            // Advance the persisted due instant to emulate downtime past the retry deadline; no event is delivered.
            await Fixture.Database.GetCollection<GitSourceExecution>("GitSourceExecutions").UpdateOneAsync(item => item.SourceId == source.Id,
                Builders<GitSourceExecution>.Update
                    .Set(item => item.NextAttemptAtUtc, DateTime.UtcNow.AddMinutes(-1))
                    .Set(item => item.Status, new ApplyingSnapshot()));
            await scope.ServiceProvider.GetRequiredService<SnapshotResumeRecoveryJob>().RecoverAsync();
        }
        var applied = (await Read(source.Id))!;
        Assert.Equal(SnapshotSourceState.ConfigurationApplied, applied.Status);
        Assert.Equal("manual-commit", applied.AppliedCommit);
        Assert.Equal(1, applied.Fence);
        Assert.Null(applied.NextAttemptAtUtc);
        var before = await Fixture.Database.GetCollection<WebServiceRelease>("WebServiceReleases").CountDocumentsAsync(Builders<WebServiceRelease>.Filter.Empty);
        Assert.Equal(1, before);
        using var resumed = await Client.PostAsJsonAsync($"{Root}/{source.Id}/resume", new { expectedFence = 1 });
        Assert.Equal(HttpStatusCode.Accepted, resumed.StatusCode);
        Assert.Equal(before, await Fixture.Database.GetCollection<WebServiceRelease>("WebServiceReleases").CountDocumentsAsync(Builders<WebServiceRelease>.Filter.Empty));
        var statusText = await Client.GetStringAsync($"{Root}/{source.Id}");
        Assert.DoesNotContain("SnapshotJson", statusText); Assert.DoesNotContain("ActorEmail", statusText);
    }

    [Fact]
    public async Task Sources_enforce_authentication_tenant_roles_and_cross_tenant_identity_on_every_route()
    {
        var source = await Create(); await Submit(source.Id);
        Client.DefaultRequestHeaders.Add("X-Test-Unauthenticated", "true");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client.GetAsync($"{Root}/{source.Id}")).StatusCode);
        Client.DefaultRequestHeaders.Remove("X-Test-Unauthenticated");
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync($"{Root}/{source.Id}")).StatusCode);
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, TestData.TenantId);
        var tenant = await Fixture.DbContext.Tenants.Find(item => item.Id == TestData.TenantId).SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(item => item.Id == tenant.Id, tenant with
        { Members = [TenantFactory.CreateMember(tenant.Id, TestData.UserId, TestData.UserEmail, TenantRole.Viewer)] });
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync($"{Root}/{source.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Submit(source.Id, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.PostAsJsonAsync($"{Root}/{source.Id}/resume", new { expectedFence = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.PostAsJsonAsync(Root + "/", new { idempotencyKey = Guid.NewGuid(), sourceUrl = source.SourceUrl })).StatusCode);
        var other = TenantFactory.Create("Other", ObjectId.GenerateNewId().ToString());
        other = other with { Members = [TenantFactory.CreateMember(other.Id, TestData.UserId, TestData.UserEmail, TenantRole.Owner)] };
        await Fixture.DbContext.Tenants.InsertOneAsync(other);
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId); Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, other.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync($"{Root}/{source.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Submit(source.Id, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PostAsJsonAsync($"{Root}/{source.Id}/resume", new { expectedFence = 1 })).StatusCode);
    }

    [Fact]
    public async Task Invalid_and_stale_submissions_cannot_change_accepted_state_or_forge_context()
    {
        var source = await Create();
        using var malformed = await Client.PutAsJsonAsync($"{Root}/{source.Id}/snapshot", new
        { expectedFence = 0, commit = "invalid", files = new[] { new { path = "bad.yaml", content = "kind: [" } } });
        var problem = await AssertProblem(malformed, HttpStatusCode.BadRequest, "gitops.invalid-snapshot");
        Assert.NotEmpty(problem.GetProperty("diagnostics").EnumerateArray());
        Assert.Equal(SnapshotSourceState.AwaitingSnapshot, (await Read(source.Id))!.Status);
        Assert.Equal(0, await Fixture.Database.GetCollection<GitSourceExecution>("GitSourceExecutions").CountDocumentsAsync(Builders<GitSourceExecution>.Filter.Empty));
        using var forged = await Client.PutAsJsonAsync($"{Root}/{source.Id}/snapshot", new
        { expectedFence = 0, commit = "forged", files = DeclarativePipelineTests.Snapshot(), tenantId = ObjectId.GenerateNewId().ToString(), actorUserId = "forged" });
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Submit(source.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Submit(source.Id)).StatusCode);
        using var staleResume = await Client.PostAsJsonAsync($"{Root}/{source.Id}/resume", new { expectedFence = 99 });
        await AssertProblem(staleResume, HttpStatusCode.Conflict, "gitops.stale-execution");
        var accepted = await Fixture.Database.GetCollection<GitSourceExecution>("GitSourceExecutions").Find(item => item.SourceId == source.Id).SingleAsync();
        Assert.Equal(TestData.UserId, accepted.ActorUserId); Assert.Equal(TestData.TenantId, accepted.TenantId); Assert.Equal(1, accepted.Fence);
    }

    [Fact]
    public async Task Manual_duplicate_names_return_conflict_and_roll_back_placement_and_outbox()
    {
        var source = await Create(); await Submit(source.Id);
        using var duplicateGroup = await Client.PostAsJsonAsync("/api/v1/resource-groups/", new { name = "DEMO", description = "manual", tags = Array.Empty<string>() });
        await AssertProblem(duplicateGroup, HttpStatusCode.Conflict, "resource-groups.name-already-exists");
        var group = await Fixture.DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("other-group"));
        var before = await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty);
        var placementCount = await Fixture.DbContext.ResourceDeployments.CountDocumentsAsync(Builders<Pole.Features.ResourceDeployments.Domain.ResourceDeployment>.Filter.Empty);
        var duplicate = new CreateWebServiceRequestBody("DEMO-API", group.Id, TestData.ValidWebServiceSpec(),
            new(null, [new(TestData.DefaultDeploymentTargetId, 1, TestData.DefaultComputeProfileId)]), []);
        using var conflict = await Client.PostAsJsonAsync("/api/v1/web-services/", duplicate);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("web-services.name-already-exists", await conflict.Content.ReadAsStringAsync());
        Assert.Equal(before, await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
        Assert.Equal(placementCount, await Fixture.DbContext.ResourceDeployments.CountDocumentsAsync(Builders<Pole.Features.ResourceDeployments.Domain.ResourceDeployment>.Filter.Empty));
    }
    [Fact]
    public async Task Concurrent_manual_group_creation_returns_one_created_and_one_typed_conflict()
    {
        var responses = await Task.WhenAll(Client.PostAsJsonAsync("/api/v1/resource-groups/", new { name = "race", description = "first", tags = Array.Empty<string>() }),
            Client.PostAsJsonAsync("/api/v1/resource-groups/", new { name = "RACE", description = "second", tags = Array.Empty<string>() }));
        Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Created);
        Assert.Single(responses, item => item.StatusCode == HttpStatusCode.Conflict);
        Assert.Single(await Fixture.DbContext.ResourceGroups.Find(Builders<Pole.Features.ResourceGroups.Domain.ResourceGroup>.Filter.Empty).ToListAsync());
        Assert.Equal(1, await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(item => item.EventType == "pole.resource-groups/v1/resource-group-created"));
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task Concurrent_source_creation_is_idempotent_and_does_not_relocate()
    {
        var key = Guid.NewGuid();
        var sources = await Task.WhenAll(Create(key), Create(key));
        Assert.Equal(sources[0].Id, sources[1].Id);
        using var conflict = await Client.PostAsJsonAsync(Root + "/", new { idempotencyKey = key, sourceUrl = "https://github.com/other/repository" });
        await AssertProblem(conflict, HttpStatusCode.Conflict, "gitops.source-relocation-required");
        Assert.Equal("https://github.com/lsbV/example", (await Read(sources[0].Id))!.SourceUrl);
    }

    [Fact]
    public async Task Conflicting_source_reports_requested_addresses_without_claiming_another_sources_inventory()
    {
        var owner = await Create(); await Submit(owner.Id);
        var requester = await Create();
        using var response = await Submit(requester.Id);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var status = (await Read(requester.Id))!;
        Assert.Equal(SnapshotSourceState.Blocked, status.Status);
        Assert.Empty(status.Resources);
        Assert.Contains(status.Operations, item => item.Kind == "WebService" && item.Name == "demo-api" && item.Blocker == "OwnershipConflict");
        Assert.All(await Fixture.Database.GetCollection<ManagedDeclaration>("ManagedDeclarations").Find(Builders<ManagedDeclaration>.Filter.Empty).ToListAsync(),
            item => Assert.Equal(owner.Id, item.SourceId));
    }

}
