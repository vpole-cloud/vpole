using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Domain;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.Secrets.Application.Endpoints;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Domain;
using Pole.Features.Tenants.Domain;
using Pole.Features.WebServices.Application.Endpoints;
using Pole.Tests.Integration.Infrastructure;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Integration.Secrets;

public sealed class SecretApiTests(MongoReplicaSetFixture fixture)
    : ApiIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Projection_status_is_tenant_scoped_reference_only_and_canonicalizes_ids()
    {
        var group = await Fixture.DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var response = await Client.PostAsJsonAsync("/api/v1/secrets/", new
        {
            resourceGroupId = group.Id,
            isolationScopeId = group.DefaultIsolationScopeId,
            name = "projection-status",
            purpose = "status",
            values = new { password = "private-status-value" }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var metadata = (await response.Content.ReadFromJsonAsync<SecretMetadata>())!;
        var secret = await Fixture.Database.GetCollection<Secret>("Secrets").Find(x => x.Id == metadata.Id).SingleAsync();
        var projection = SecretProjectionFactory.Create(secret,
            SecretBinding.Create(ObjectId.GenerateNewId().ToString(), "pole/web-service", ObjectId.GenerateNewId().ToString(), new AllSecretDeployments(), []),
            ObjectId.GenerateNewId().ToString(), "cell", "target", "private-namespace").Failed("ProjectionUnavailable");
        await Fixture.Database.GetCollection<SecretProjection>("SecretProjections").InsertOneAsync(projection);
        var body = await Client.GetStringAsync($"/api/v1/secrets/{metadata.Id.ToUpperInvariant()}/projections");
        var status = Assert.Single(JsonSerializer.Deserialize<SecretProjectionStatusDto[]>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        Assert.Equal("Failed", status.Health);
        Assert.Equal("ProjectionUnavailable", status.FailureCode);
        Assert.Equal(projection.DeploymentId, status.ResourceDeploymentId);
        Assert.DoesNotContain("private-status-value", body);
        Assert.DoesNotContain("private-namespace", body);
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync($"/api/v1/secrets/{metadata.Id}/projections")).StatusCode);
    }

    [Fact]
    public async Task WriteOnlyApiReturnsMetadata_ValidatesInput_AndPaginates()
    {
        var group = await Fixture.DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        object Body(string name) => new
        {
            resourceGroupId = group.Id,
            isolationScopeId = group.DefaultIsolationScopeId,
            name,
            purpose = "api test",
            values = new Dictionary<string, string>
            {
                ["password"] = "synthetic-api-password"
            }
        };
        var created = await Client.PostAsJsonAsync("/api/v1/secrets/", Body("one"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var metadata = await created.Content.ReadFromJsonAsync<SecretMetadata>();
        Assert.NotNull(metadata);
        var duplicateName = await Client.PostAsJsonAsync("/api/v1/secrets/", Body(" ONE "));
        Assert.Equal(HttpStatusCode.Conflict, duplicateName.StatusCode);
        await AssertProblemCode(duplicateName, "secrets.name-already-exists");
        var second = await Client.PostAsJsonAsync("/api/v1/secrets/", Body("two"));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var get = await Client.GetStringAsync($"/api/v1/secrets/{metadata.Id}");
        Assert.DoesNotContain("synthetic-api-password", get);
        Assert.DoesNotContain("ciphertext", get, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("wrapped", get, StringComparison.OrdinalIgnoreCase);
        var firstPage = await Client.GetFromJsonAsync<SecretPage>("/api/v1/secrets/?pageSize=1");
        Assert.NotNull(firstPage);
        Assert.Single(firstPage.Items);
        Assert.NotNull(firstPage.NextCursor);
        var nextPage = await Client.GetFromJsonAsync<SecretPage>($"/api/v1/secrets/?pageSize=1&after={firstPage.NextCursor}");
        Assert.NotNull(nextPage);
        Assert.Single(nextPage.Items);
        Assert.Null(nextPage.NextCursor);
        Assert.NotEqual(firstPage.Items[0].Id, nextPage.Items[0].Id);
        var rotated = await Client.PostAsJsonAsync(
            $"/api/v1/secrets/{metadata.Id}/versions",
            new
            {
                expectedVersion = 1,
                values = new
                {
                    password = "synthetic-rotated-password"
                }
            });
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        Assert.DoesNotContain("synthetic-rotated-password", await rotated.Content.ReadAsStringAsync());
        var stale = await Client.PostAsJsonAsync(
            $"/api/v1/secrets/{metadata.Id}/versions",
            new
            {
                expectedVersion = 1,
                values = new
                {
                    password = "synthetic-rotated-password"
                }
            });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await AssertProblemCode(stale, "secrets.version-conflict");
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/v1/secrets/bad-id")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/v1/secrets/?pageSize=0")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client.DeleteAsync($"/api/v1/secrets/{metadata.Id}")).StatusCode);
    }

    [Fact]
    public async Task DuplicateNameInAnotherResourceGroupReturnsConflict()
    {
        var first = await Fixture.DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("first"));
        var second = await Fixture.DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup("second"));
        object Body(ResourceGroup group, string name) => new
        {
            resourceGroupId = group.Id,
            isolationScopeId = group.DefaultIsolationScopeId,
            name,
            purpose = "tenant name uniqueness",
            values = new { password = "synthetic" }
        };
        using var created = await Client.PostAsJsonAsync("/api/v1/secrets/", Body(first, "shared-name"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var conflict = await Client.PostAsJsonAsync("/api/v1/secrets/", Body(second, " SHARED-NAME "));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await AssertProblemCode(conflict, "secrets.name-already-exists");
    }

    [Fact]
    public async Task TenantAndRoleAuthorizationRejectMissingSelectionForeignTenantAndViewerWrites()
    {
        var group = await Fixture.DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        object body = new
        {
            resourceGroupId = group.Id,
            isolationScopeId = group.DefaultIsolationScopeId,
            name = "auth",
            purpose = "api test",
            values = new
            {
                password = "synthetic-auth-password"
            }
        };
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client.GetAsync("/api/v1/secrets/")).StatusCode);
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, TestData.TenantId);
        var tenant = await Fixture.DbContext.Tenants.Find(x => x.Id == TestData.TenantId).SingleAsync();
        var viewer = tenant with
        {
            Members = ImmutableList.Create(TenantFactory.CreateMember(
                TestData.TenantId,
                TestData.UserId,
                TestData.UserEmail,
                TenantRole.Viewer))
        };
        await Fixture.DbContext.Tenants.ReplaceOneAsync(x => x.Id == tenant.Id, viewer);
        Assert.Equal(HttpStatusCode.OK, (await Client.GetAsync("/api/v1/secrets/")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await Client.PostAsJsonAsync("/api/v1/secrets/", body)).StatusCode);
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, ObjectId.GenerateNewId().ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await Client.GetAsync("/api/v1/secrets/")).StatusCode);
    }

    [Fact]
    public async Task UppercaseObjectIdsUseCanonicalOwnershipAndScopeUniqueness()
    {
        var group = await Fixture.DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var scope = IsolationScopeFactory.CreateAdditional(
            group.TenantId,
            group.Id,
            "uppercase",
            IsolationScopeFactory.DefaultOptions);
        await Fixture.DbContext.IsolationScopes.InsertOneAsync(scope);
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, group.TenantId.ToUpperInvariant());
        var create = await Client.PostAsJsonAsync(
            "/api/v1/secrets/",
            new
            {
                resourceGroupId = group.Id.ToUpperInvariant(),
                isolationScopeId = scope.Id.ToUpperInvariant(),
                name = "canonical",
                purpose = "normalization",
                values = new
                {
                    password = "synthetic"
                }
            });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var secret = await create.Content.ReadFromJsonAsync<SecretMetadata>();
        Assert.NotNull(secret);
        Assert.Equal(group.TenantId, secret.TenantId);
        Assert.Equal(group.Id, secret.ResourceGroupId);
        Assert.Equal(scope.Id, secret.IsolationScopeId);
        Assert.Equal(
            HttpStatusCode.OK,
            (await Client.GetAsync($"/api/v1/secrets/{secret.Id.ToUpperInvariant()}")).StatusCode);
        var page = await Client.GetFromJsonAsync<SecretPage>("/api/v1/secrets/");
        Assert.NotNull(page);
        Assert.Equal(secret.Id, Assert.Single(page.Items).Id);
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, group.TenantId);
        var duplicate = await Client.PostAsJsonAsync(
            "/api/v1/secrets/",
            new
            {
                resourceGroupId = group.Id,
                isolationScopeId = scope.Id,
                name = "CANONICAL",
                purpose = "normalization",
                values = new
                {
                    password = "synthetic"
                }
            });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await AssertProblemCode(duplicate, "secrets.name-already-exists");
        var deletion = await Client.DeleteAsync($"/api/v1/resource-groups/{group.Id}/isolation-scopes/{scope.Id}");
        Assert.Equal(HttpStatusCode.Conflict, deletion.StatusCode);
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, group.TenantId.ToUpperInvariant());
        var deletedSecret = await Client.DeleteAsync($"/api/v1/secrets/{secret.Id.ToUpperInvariant()}");
        Assert.Equal(HttpStatusCode.NoContent, deletedSecret.StatusCode);
        Assert.False(await Fixture.DbContext.ResourceRegistrations
            .Find(x => x.ResourceId == secret.Id)
            .AnyAsync());
        Client.DefaultRequestHeaders.Remove(TenantHeaders.TenantId);
        Client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, group.TenantId);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await Client.DeleteAsync($"/api/v1/resource-groups/{group.Id}/isolation-scopes/{scope.Id}")).StatusCode);
    }

    [Fact]
    public async Task PaddedResourceTypeAndUppercaseIdsBindCanonicallyAndCleanupOnUnregister()
    {
        var group = await Fixture.DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var create = await Client.PostAsJsonAsync(
            "/api/v1/secrets/",
            new
            {
                resourceGroupId = group.Id,
                isolationScopeId = group.DefaultIsolationScopeId,
                name = "canonical-binding",
                purpose = "normalization",
                values = new
                {
                    password = "synthetic"
                }
            });
        var secret = await create.Content.ReadFromJsonAsync<SecretMetadata>();
        Assert.NotNull(secret);
        var createdConsumer = await Client.PostAsJsonAsync(
            "/api/v1/web-services/",
            TestData.CreateWebServiceRequest(group.Id).Body);
        Assert.Equal(HttpStatusCode.Accepted, createdConsumer.StatusCode);
        var consumer = await createdConsumer.Content.ReadFromJsonAsync<CreateWebServiceResponse>();
        Assert.NotNull(consumer);
        var deployment = Assert.Single(consumer.Deployments);
        var unknownKeys = await Client.PostAsJsonAsync(
            $"/api/v1/secrets/{secret.Id}/bindings",
            new
            {
                consumerType = "pole/web-service",
                consumerId = consumer.WebServiceId,
                allDeployments = true,
                deploymentIds = Array.Empty<string>(),
                keyMappings = new Dictionary<string, string>
                {
                    ["missing"] = "MISSING"
                }
            });
        Assert.Equal(HttpStatusCode.Conflict, unknownKeys.StatusCode);
        await AssertProblemCode(unknownKeys, "secrets.unknown-keys");
        var bound = await Client.PostAsJsonAsync(
            $"/api/v1/secrets/{secret.Id.ToUpperInvariant()}/bindings",
            new
            {
                consumerType = " pole/web-service ",
                consumerId = consumer.WebServiceId.ToUpperInvariant(),
                allDeployments = false,
                deploymentIds = new[]
                {
                    deployment.DeploymentId.ToUpperInvariant()
                },
                keyMappings = new Dictionary<string, string>
                {
                    ["password"] = "PASSWORD"
                }
            });
        Assert.Equal(HttpStatusCode.OK, bound.StatusCode);
        var removeMappedKey = await Client.PostAsJsonAsync(
            $"/api/v1/secrets/{secret.Id}/versions",
            new
            {
                expectedVersion = 1,
                values = new
                {
                    other = "replacement"
                }
            });
        Assert.Equal(HttpStatusCode.Conflict, removeMappedKey.StatusCode);
        await AssertProblemCode(removeMappedKey, "secrets.mapped-keys-required");
        var boundDeletion = await Client.DeleteAsync($"/api/v1/secrets/{secret.Id}");
        Assert.Equal(HttpStatusCode.Conflict, boundDeletion.StatusCode);
        await AssertProblemCode(boundDeletion, "secrets.bindings-remain");
        var usage = await Client.GetFromJsonAsync<SecretUsage[]>($"/api/v1/secrets/{secret.Id}/usage");
        Assert.NotNull(usage);
        var binding = Assert.Single(usage).Binding;
        Assert.Equal("pole/web-service", binding.ConsumerType);
        Assert.Equal(consumer.WebServiceId, binding.ConsumerId);
        Assert.Equal(deployment.DeploymentId, Assert.Single(binding.DeploymentIds));
        var duplicate = await Client.PostAsJsonAsync(
            $"/api/v1/secrets/{secret.Id}/bindings",
            new
            {
                consumerType = "pole/web-service",
                consumerId = consumer.WebServiceId,
                allDeployments = true,
                deploymentIds = Array.Empty<string>()
            });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        await AssertProblemCode(duplicate, "secrets.consumer-already-bound");
        using var services = TestServiceProviderFactory.Create(Fixture);
        using var serviceScope = services.CreateScope();
        using var session = await Fixture.Client.StartSessionAsync();
        session.StartTransaction();
        await serviceScope.ServiceProvider.GetRequiredService<IResourceRegistrar>().UnregisterAsync(
            new UnregisterResourceRequest(
                TenantId: group.TenantId,
                ResourceGroupId: group.Id,
                ResourceTypeId: ResourceTypeId.Create("pole/web-service"),
                ResourceId: consumer.WebServiceId),
            session: session);
        await session.CommitTransactionAsync();
        var cleanup = new RemoveSecretBindingsOnResourceUnregistered(serviceScope.ServiceProvider.GetRequiredService<RemoveConsumerSecretBindings>());
        await cleanup.Handle(new ResourceUnregisteredEvent(group.Id, "pole/web-service", consumer.WebServiceId)
        {
            EventCore = EventCore.Create(group.TenantId)
        });
        var cleaned = await Client.GetFromJsonAsync<SecretUsage[]>($"/api/v1/secrets/{secret.Id}/usage");
        Assert.NotNull(cleaned);
        Assert.Empty(cleaned);
    }

    private static async Task AssertProblemCode(
        HttpResponseMessage response,
        string expected)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, body.RootElement.GetProperty("code").GetString());
    }
}
