using System.Net;
using Pole.Tests.Integration.Http;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.GitOps.Application.Endpoints.Sources;
using Pole.Features.GitOps.Domain;
using Pole.Features.GitOps.Primitives;
using Pole.Features.Secrets.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Features.WebServices.Application.Common;
using Pole.Features.WebServices.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.GitOps;

[Collection(IntegrationTestCollection.Tenant1)]
public sealed class RepositoryAdoptionCandidateApiTests(MongoReplicaSetFixture fixture) : RepositorySourceApiTestBase(fixture)
{
    [Fact]
    public async Task Candidate_search_resolves_manual_identity_and_reports_deleting_or_owned_resources()
    {
        var source = await Create(files: [GroupManifest("owned")]);
        await Run(source.Id);
        var manual = await Fixture.DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup("manual"));
        var candidate = Assert.Single((await Candidates(source.Id, "kind=ResourceGroup&name=manual")).Items);
        Assert.Equal(manual.Id, candidate.ResourceId);
        Assert.True(candidate.Selectable);
        Assert.Null(candidate.OwnerSourceId);
        Assert.Null(candidate.Blocker);
        var owned = Assert.Single((await Candidates(source.Id, "kind=ResourceGroup&name=owned")).Items);
        Assert.False(owned.Selectable);
        Assert.Equal(source.Id, owned.OwnerSourceId);
        Assert.Equal("OwnershipConflict", owned.Blocker);
        await Fixture.DbContext.ResourceGroups.ReplaceOneAsync(item => item.Id == manual.Id,
            manual.RequestDeletion(DateTime.UtcNow));
        candidate = Assert.Single((await Candidates(source.Id, "kind=ResourceGroup&name=manual")).Items);
        Assert.False(candidate.Selectable);
        Assert.Equal(GitLifecyclePresence.Deleting, candidate.Presence);
        Assert.Equal("ResourceDeleting", candidate.Blocker);
    }

    [Fact]
    public async Task Candidate_search_does_not_turn_a_paused_sources_resource_into_a_manual_candidate()
    {
        var owner = await Create(files: [GroupManifest("owned")]);
        await Run(owner.Id);
        using var paused = await Client.PostAsJsonAsync($"{Root}/{owner.Id}/pause", new { expectedRevision = 1 });
        Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        var requester = await Create(files: [GroupManifest("requester")]);
        var candidate = Assert.Single((await Candidates(requester.Id, "kind=ResourceGroup&name=owned")).Items);
        Assert.False(candidate.Selectable);
        Assert.Equal(owner.Id, candidate.OwnerSourceId);
    }

    [Fact]
    public async Task Candidate_search_keeps_unnamed_binding_metadata_and_never_returns_secret_configuration()
    {
        var source = await Create();
        await Run(source.Id);
        using var scope = ApiServices.CreateScope();
        var secrets = scope.ServiceProvider.GetRequiredService<IMongoCollection<Secret>>();
        var secret = await secrets.Find(item => item.TenantId == Data.TenantId).SingleAsync();
        var binding = Assert.Single(secret.Bindings);
        var placement = await Fixture.DbContext.ResourceDeployments.Find(item => item.TenantId == Data.TenantId).SingleAsync();
        await secrets.ReplaceOneAsync(item => item.Id == secret.Id, secret with
        {
            Purpose = "private-purpose-not-for-candidates",
            Bindings =
            [binding with { DeclarationName = null, Selection = new SelectedSecretDeployments([placement.Id]) }]
        });
        await scope.ServiceProvider.GetRequiredService<IMongoCollection<ManagedDeclaration>>()
            .DeleteOneAsync(item => item.Kind == "SecretBinding" && item.ResourceId == binding.Id);
        const string query = "kind=SecretBinding&relatedKind=Secret&relatedName=demo-api-credentials&" +
                             "consumerKind=WebService&consumerName=demo-api";
        var candidate = Assert.Single((await Candidates(source.Id, query + "&pageSize=1")).Items);
        Assert.Null(candidate.Name);
        Assert.Equal(binding.Id, candidate.ResourceId);
        Assert.True(candidate.Selectable);
        Assert.Equal(secret.Id, candidate.Binding!.Secret.ResourceId);
        Assert.Equal("demo-api", candidate.Binding.Consumer.Name);
        Assert.Equal("Runtime", candidate.Binding.Usage);
        Assert.Equal("Selected", candidate.Binding.TargetScope.Mode);
        Assert.Equal([placement.Id], candidate.Binding.TargetScope.DeploymentIds);
        Assert.Empty((await Candidates(source.Id, query + "&page=2&pageSize=1")).Items);
        var json = await (await Client.GetAsync($"{Root}/{source.Id}/adoption-candidates?{query}"))
            .Content.ReadAsStringAsync();
        foreach (var field in new[] { "private-purpose-not-for-candidates", "keyMappings", "suppliedKeys",
                     "currentVersion", "encrypted", "specJson", "dockerfile" })
            Assert.DoesNotContain(field, json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Assert.Single((await secrets.Find(item => item.TenantId == Data.TenantId).SingleAsync()).Bindings).DeclarationName);
        Assert.False(await scope.ServiceProvider.GetRequiredService<IMongoCollection<ManagedDeclaration>>()
            .Find(item => item.TenantId == Data.TenantId && item.Kind == "SecretBinding" && item.ResourceId == binding.Id).AnyAsync());
    }

    [Fact]
    public async Task Candidate_binding_search_does_not_rebind_an_old_consumer_after_technical_name_reuse()
    {
        var source = await Create();
        await Run(source.Id);
        using var scope = ApiServices.CreateScope();
        var services = scope.ServiceProvider.GetRequiredService<IMongoCollection<WebService>>();
        var original = await services.Find(item => item.TenantId == Data.TenantId).SingleAsync();
        await services.DeleteOneAsync(item => item.Id == original.Id);
        await services.InsertOneAsync(original with { Id = ObjectId.GenerateNewId().ToString() });
        Assert.Empty((await Candidates(source.Id,
            "kind=SecretBinding&relatedKind=Secret&relatedName=demo-api-credentials&" +
            "consumerKind=WebService&consumerName=demo-api")).Items);
    }

    [Fact]
    public async Task Candidate_search_omits_provider_managed_credentials_and_system_default_scopes()
    {
        var source = await Create();
        await Run(source.Id);
        using var scope = ApiServices.CreateScope();
        var secrets = scope.ServiceProvider.GetRequiredService<IMongoCollection<Secret>>();
        var secret = await secrets.Find(item => item.TenantId == Data.TenantId).SingleAsync();
        var binding = Assert.Single(secret.Bindings);
        await secrets.ReplaceOneAsync(item => item.Id == secret.Id, secret with
        {
            Management = new ProviderManagedSecret(binding.Consumer.ResourceType, binding.Consumer.ResourceId,
                binding.Id, KubernetesSecretType.Opaque)
        });
        Assert.Empty((await Candidates(source.Id, "kind=Secret&name=demo-api-credentials")).Items);
        Assert.Empty((await Candidates(source.Id, "kind=IsolationScope&name=default")).Items);
        Assert.Empty((await Candidates(source.Id,
            "kind=SecretBinding&relatedKind=Secret&relatedName=demo-api-credentials&" +
            "consumerKind=WebService&consumerName=demo-api")).Items);
    }

    [Theory]
    [InlineData("kind=Unknown&name=manual")]
    [InlineData("kind=ResourceGroup")]
    [InlineData("kind=ResourceGroup&name=bad_name")]
    [InlineData("kind=ResourceGroup&name=manual&pageSize=101")]
    [InlineData("kind=ResourceGroup&relatedKind=Secret&relatedName=auth&consumerKind=WebService&consumerName=app")]
    [InlineData("kind=SecretBinding&relatedKind=Secret&relatedName=auth")]
    [InlineData("kind=SecretBinding&name=both&relatedKind=Secret&relatedName=auth&" +
                "consumerKind=WebService&consumerName=app")]
    public async Task Candidate_search_rejects_unknown_or_mixed_filters_and_unbounded_pages(string query)
    {
        var source = await Create(files: [GroupManifest("source")]);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Client.GetAsync($"{Root}/{source.Id}/adoption-candidates?{query}")).StatusCode);
    }

    [Fact]
    public async Task Candidate_search_allows_viewers_and_isolates_both_source_and_related_resource_tenants()
    {
        var source = await Create(files: [GroupManifest("source")]);
        var foreign = TenantFactory.Create("Foreign", ObjectId.GenerateNewId().ToString());
        foreign = foreign with
        {
            Members = [TenantFactory.CreateMember(foreign.Id, Data.UserId, Data.UserEmail, TenantRole.Owner)]
        };
        await Fixture.DbContext.Tenants.InsertOneAsync(foreign);
        var foreignGroup = await Fixture.DbContext.InsertResourceGroupAsync(
            Data.SeedResourceGroup("foreign-only", foreign.Id));
        using var scope = ApiServices.CreateScope();
        var foreignConsumer = Data.SeedWebService(foreignGroup.Id, foreign.Id, "foreign-api");
        await scope.ServiceProvider.GetRequiredService<IMongoCollection<WebService>>().InsertOneAsync(foreignConsumer);
        var foreignBinding = SecretBinding.Create(ObjectId.GenerateNewId().ToString(),
            WebServiceResourceDeployment.ResourceType.Value, foreignConsumer.Id, new AllSecretDeployments(), []);
        var foreignSecret = SecretFactory.CreateMetadata(ObjectId.GenerateNewId().ToString(), foreign.Id,
            foreignGroup.Id, foreignGroup.DefaultIsolationScopeId, "foreign-auth", "private-purpose", [],
            DateTime.UtcNow)
            .Bind(foreignBinding, DateTime.UtcNow);
        await scope.ServiceProvider.GetRequiredService<IMongoCollection<Secret>>().InsertOneAsync(foreignSecret);
        var tenant = await Fixture.DbContext.Tenants.Find(item => item.Id == Data.TenantId).SingleAsync();
        await Fixture.DbContext.Tenants.ReplaceOneAsync(item => item.Id == tenant.Id, tenant with
        {
            Members = [TenantFactory.CreateMember(tenant.Id, Data.UserId, Data.UserEmail, TenantRole.Viewer)]
        });
        Assert.Empty((await Candidates(source.Id, "kind=ResourceGroup&name=foreign-only")).Items);
        Assert.Empty((await Candidates(source.Id,
            "kind=SecretBinding&relatedKind=Secret&relatedName=foreign-auth&" +
            "consumerKind=WebService&consumerName=foreign-api")).Items);
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, foreign.Id);
        Assert.Equal(HttpStatusCode.NotFound,
            (await Client.GetAsync($"{Root}/{source.Id}/adoption-candidates?kind=ResourceGroup&name=foreign-only"))
            .StatusCode);
    }

    [Theory]
    [InlineData("kind=Unknown&name=manual")]
    [InlineData("kind=ResourceGroup&relatedKind=Secret&relatedName=auth&consumerKind=WebService&consumerName=app")]
    public async Task Candidate_unsupported_search_returns_the_stable_typed_problem(string query)
    {
        var source = await Create(files: [GroupManifest("source")]);
        using var response = await Client.GetAsync($"{Root}/{source.Id}/adoption-candidates?{query}");
        await AssertProblem(response, HttpStatusCode.BadRequest, "gitops.candidate-query-unsupported");
    }
}
