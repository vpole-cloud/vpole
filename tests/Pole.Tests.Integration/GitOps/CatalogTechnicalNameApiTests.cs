using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.ComputeProfiles.Application.Common;
using Pole.Features.ComputeProfiles.Application.Endpoints;
using Pole.Features.ComputeProfiles.Migrations;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.Outbox.Domain;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Application.Endpoints;
using Pole.Features.DeploymentTargets.Migrations;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.GitOps.Application.Persistence;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.ComputeProfiles;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

public sealed class CatalogTechnicalNameApiTests(MongoReplicaSetFixture fixture) : ApiIntegrationTestBase(fixture)
{
    protected override void ConfigureTestServices(IServiceCollection services) =>
        NamedCatalogTestData.ConfigureIsolatedApi(services);
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        await new ComputeProfileSchemaValidationMigration().Up(Fixture.Database);
        await new ComputeProfileHistoryMigration().Up(Fixture.Database);
        await NamedCatalogTestData.SeedAsync(Fixture);
    }
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private IMongoCollection<ComputeProfile> Profiles =>
        Fixture.Database.GetCollection<ComputeProfile>("ComputeProfiles");
    private static string Root(string catalog) => $"/api/v1/platform/{catalog}/";

    [Fact]
    public async Task Isolated_api_fixture_rejects_every_external_client_lookup()
    {
        var services = new ServiceCollection();
        NamedCatalogTestData.ConfigureIsolatedApi(services);
        using var provider = services.BuildServiceProvider();
        var clients = provider.GetRequiredService<IKubernetesClientProvider>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => clients.GetAsync(TestData.DefaultDeploymentTargetId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            clients.GetForCleanupAsync(TestData.DefaultDeploymentTargetId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => clients.GetDefaultAsync());
    }
    internal static JsonObject Body(string catalog, string? name,
        string targetId = TestData.DefaultDeploymentTargetId) =>
        JsonSerializer.SerializeToNode(catalog == "compute-profiles"
            ? (object)new CreateComputeProfileRequestBody(name!, "General purpose", "Profile", new(new(250, 256),
                new(1000, 1024)),
                "Any", [], ["pole/web-service"], new("all-tenants", []), [new(targetId, [], [], [], [], null)], true,
                    false)
            : DeploymentTargetApiTests.CreateBody("Target", "https://" + (name ?? "missing") + ".test:6443",
                DeploymentTargetApiTests.CreateCertificateAuthorityData(), "synthetic-catalog-token") with
            { TechnicalName = name! }, JsonOptions)!.AsObject();

    [Theory]
    [InlineData("compute-profiles", "missing")]
    [InlineData("deployment-targets", "missing")]
    [InlineData("compute-profiles", "null")]
    [InlineData("deployment-targets", "null")]
    [InlineData("compute-profiles", "empty")]
    [InlineData("deployment-targets", "empty")]
    [InlineData("compute-profiles", "invalid")]
    [InlineData("deployment-targets", "invalid")]
    public async Task Creation_requires_explicit_valid_technical_name_before_any_mutation(string catalog,
        string scenario)
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var body = Body(catalog, "valid-name");
        if (scenario == "missing") body.Remove("technicalName");
        else body["technicalName"] = scenario switch { "null" => null, "empty" => "", _ => "Not a technical name" };
        var before = await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty);
        using var response = await Client.PostAsJsonAsync(Root(catalog), body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before,
            await Fixture.DbContext.OutboxMessages.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
    }

    [Theory]
    [InlineData("compute-profiles")]
    [InlineData("deployment-targets")]
    public async Task One_admin_create_request_sets_name_at_revision_one_and_assignment_route_is_removed(string catalog)
    {
        var body = Body(catalog, "new-entry");
        Assert.Equal(HttpStatusCode.Forbidden, (await Client.PostAsJsonAsync(Root(catalog), body)).StatusCode);
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        using var created = await Client.PostAsJsonAsync(Root(catalog), body);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var value = (await created.Content.ReadFromJsonAsync<JsonElement>());
        Assert.Equal("new-entry", value.GetProperty("technicalName").GetString());
        Assert.Equal(1, value.GetProperty("revision").GetInt64());
        var id = value.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await Client.PutAsJsonAsync(Root(catalog) + id + "/technical-name",
            new { name = "renamed", expectedRevision = 1 })).StatusCode);
    }

    [Theory]
    [InlineData("compute-profiles")]
    [InlineData("deployment-targets")]
    public async Task Concurrent_duplicate_creation_returns_one_created_and_one_name_conflict(string catalog)
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var first = Body(catalog, "unique-name");
        var second = Body(catalog, "other-cluster");
        second["technicalName"] = "unique-name";
        var responses = await Task.WhenAll(Client.PostAsJsonAsync(Root(catalog), first),
            Client.PostAsJsonAsync(Root(catalog), second));
        Assert.Single(responses, result => result.StatusCode == HttpStatusCode.Created);
        var conflict = Assert.Single(responses, result => result.StatusCode == HttpStatusCode.Conflict);
        Assert.Contains("technical-name-conflict", await conflict.Content.ReadAsStringAsync());
        foreach (var response in responses) response.Dispose();
    }

    [Theory]
    [InlineData("compute-profiles")]
    [InlineData("deployment-targets")]
    public async Task Normal_updates_preserve_technical_name_and_explicit_rename_is_rejected(string catalog)
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        var body = Body(catalog, "stable-name");
        using var created = await Client.PostAsJsonAsync(Root(catalog), body);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var value = (await created.Content.ReadFromJsonAsync<JsonElement>());
        var id = value.GetProperty("id").GetString();
        body.Remove("technicalName"); body.Remove("connection"); body["revision"] = 1;
        body["displayName"] = "A new display label";
        using var updated = await Client.PutAsJsonAsync(Root(catalog) + id, body);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var result = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stable-name", result.GetProperty("technicalName").GetString());
        Assert.Equal(2, result.GetProperty("revision").GetInt64());
        body["revision"] = 2; body["technicalName"] = "renamed";
        using var rejected = await Client.PutAsJsonAsync(Root(catalog) + id, body);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        if (catalog == "compute-profiles")
        {
            var history = await Fixture.Database.GetCollection<ComputeProfileRevision>("ComputeProfileRevisions")
                .Find(item => item.ComputeProfileId == id).SortBy(item => item.Revision).ToListAsync();
            Assert.Equal([1L, 2L], history.Select(item => item.Revision).ToArray());
            Assert.All(history, item => Assert.Equal("stable-name", item.Snapshot.TechnicalName));
        }
    }

    [Fact]
    public async Task Profile_resolution_checks_tenant_resource_target_and_explicit_history_revision()
    {
        Client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        using var created = await Client.PostAsJsonAsync(Root("compute-profiles"), Body("compute-profiles", "pinned"));
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var profile = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        await Profiles.UpdateOneAsync(item => item.Id == profile,
            Builders<ComputeProfile>.Update.Set(item => item.TenantAccess,
                SelectedTenantsComputeProfileAccess.Create([TestData.TenantId])));
        using var provider = TestServiceProviderFactory.Create(Fixture);
        using var scope = provider.CreateScope();
        var resolve = scope.ServiceProvider.GetRequiredService<ResolveNamedComputeProfile>();
        var request = new NamedComputeProfileRequest(TestData.TenantId, ResourceTypeId.Create("pole/web-service"),
            "pinned", TestData.DefaultDeploymentTargetId, 1);
        var accepted = Assert.IsType<ResolvedComputeProfile>((await resolve(request)).Value);
        Assert.Equal(profile, accepted.ComputeProfileId); Assert.Equal(1, accepted.ComputeProfileRevision);
        Assert.IsType<NamedComputeProfileUnavailable>((await resolve(request with
        {
            TenantId =
            ObjectId.GenerateNewId().ToString()
        })).Value);
        Assert.IsType<NamedComputeProfileUnavailable>((await resolve(request with
        {
            ResourceType =
            ResourceTypeId.Create("unavailable/type")
        })).Value);
        Assert.IsType<NamedComputeProfileUnavailable>((await resolve(request with
        {
            DeploymentTargetId =
            ObjectId.GenerateNewId().ToString()
        })).Value);
        Assert.IsType<NamedComputeProfileUnavailable>((await resolve(request with { Name = "unknown" })).Value);
        Assert.Equal(ComputeProfileUnavailableReason.RevisionUnavailable,
            Assert.IsType<ComputeProfileUnavailable>((await resolve(request with { Revision = 99 })).Value).Reason);
    }

    [Fact]
    public async Task Ambiguous_names_in_corrupted_catalogs_are_rejected_instead_of_choosing_a_match()
    {
        await NamedCatalogTestData.SeedAsync(Fixture);
        await Profiles.Indexes.DropOneAsync("UX_ComputeProfiles_TechnicalName");
        await Profiles.InsertOneAsync(TestData.SeedComputeProfile(ObjectId.GenerateNewId().ToString(), isDefault: false)
            with
        { TechnicalName = NamedCatalogTestData.ProfileName });
        using var provider = TestServiceProviderFactory.Create(Fixture);
        using var scope = provider.CreateScope();
        Assert.IsType<NamedComputeProfileAmbiguous>((
            await scope.ServiceProvider.GetRequiredService<ResolveNamedComputeProfile>()(
            new(TestData.TenantId, ResourceTypeId.Create("pole/web-service"), NamedCatalogTestData.ProfileName,
                TestData.DefaultDeploymentTargetId, 1))).Value);
        await Fixture.DbContext.DeploymentTargets.Indexes.DropOneAsync("UX_DeploymentTargets_TechnicalName");
        await Fixture.DbContext.DeploymentTargets.InsertOneAsync(ComputeProfileTestData.CreateTarget()
            with
        { TechnicalName = NamedCatalogTestData.TargetName });
        Assert.IsType<NamedDeploymentTargetAmbiguous>((
            await scope.ServiceProvider.GetRequiredService<ResolveNamedDeploymentTarget>()(
            new(TestData.TenantId, ResourceTypeId.Create("pole/web-service"), NamedCatalogTestData.TargetName))).Value);
    }
}
