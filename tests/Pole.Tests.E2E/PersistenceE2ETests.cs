using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pole.Tests.E2E.Infrastructure;
using Xunit;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Pole.Tests.E2E;

[Collection(PoleE2ECollection.Name)]
public sealed class PersistenceE2ETests(PoleE2EFixture fixture)
{
    [Fact]
    public async Task Resource_group_CRUD_is_tenant_scoped_and_survives_server_restart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = timeout.Token;
        var name = $"db-{Guid.NewGuid():N}"[..20];
        using var create = fixture.CreateAuthenticatedRequest(HttpMethod.Post, "/api/v1/resource-groups",
            fixture.Alpha, fixture.Alpha.DefaultTenantId);
        create.Content = JsonContent.Create(new { name, description = "before restart", tags = new[] { "e2e" } });
        using var created = await fixture.Api.SendAsync(create, ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var document = await created.Content.ReadFromJsonAsync<JsonElement>(ct);
        var id = document.GetProperty("id").GetString()!;
        try
        {
            using var update = fixture.CreateAuthenticatedRequest(HttpMethod.Put, $"/api/v1/resource-groups/{id}",
                fixture.Alpha, fixture.Alpha.DefaultTenantId);
            update.Content = JsonContent.Create(new { description = "after update", tags = new[] { "persisted" } });
            using var updated = await fixture.Api.SendAsync(update, ct);
            updated.EnsureSuccessStatusCode();

            var own = await ListAsync(fixture.Alpha, ct);
            Assert.Contains(own, item => item.GetProperty("id").GetString() == id);
            var foreign = await ListAsync(fixture.Beta, ct);
            Assert.DoesNotContain(foreign, item => item.GetProperty("id").GetString() == id);
            using var foreignUpdate = fixture.CreateAuthenticatedRequest(HttpMethod.Put, $"/api/v1/resource-groups/{id}",
                fixture.Beta, fixture.Beta.DefaultTenantId);
            foreignUpdate.Content = JsonContent.Create(new { description = "foreign", tags = Array.Empty<string>() });
            using var rejected = await fixture.Api.SendAsync(foreignUpdate, ct);
            Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);

            await fixture.WaitForOutboxPublicationAsync("pole.resource-groups/v1/resource-group-updated", ct);
            // Publication and projection commit are asynchronous. Inspect only while
            // the file is closed; restart consumers and retry if delivery is still pending.
            var projected = false;
            while (!projected)
            {
                await fixture.RestartServerAsync(ct, async database =>
                {
                    var identity = new BsonDocument { ["SourceId"] = id, ["TenantId"] = fixture.Alpha.DefaultTenantId };
                    var projection = await database.GetCollection<BsonDocument>("SearchSourceDocuments")
                        .Find(identity).FirstOrDefaultAsync(ct);
                    var version = await database.GetCollection<BsonDocument>("SearchProjectionVersions")
                        .Find(identity).FirstOrDefaultAsync(ct);
                    projected = projection is not null && projection["Title"].AsString == name &&
                        version is not null && version["ResourceRevision"].ToInt64() >= 2;
                });
                if (!projected)
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            var reopened = await ListAsync(fixture.Alpha, ct);
            var persisted = Assert.Single(reopened, item => item.GetProperty("id").GetString() == id);
            Assert.Equal("after update", persisted.GetProperty("description").GetString());
            Assert.Contains("persisted", persisted.GetProperty("tags").EnumerateArray().Select(item => item.GetString()));
        }
        finally
        {
            using var delete = fixture.CreateAuthenticatedRequest(HttpMethod.Delete, $"/api/v1/resource-groups/{id}",
                fixture.Alpha, fixture.Alpha.DefaultTenantId);
            using var deleted = await fixture.Api.SendAsync(delete, ct);
            deleted.EnsureSuccessStatusCode();
        }
        while ((await ListAsync(fixture.Alpha, ct)).Any(item => item.GetProperty("id").GetString() == id))
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
    }

    private async Task<JsonElement[]> ListAsync(TestUser user, CancellationToken ct)
    {
        using var request = fixture.CreateAuthenticatedRequest(HttpMethod.Get, "/api/v1/resource-groups?PageNumber=1&PageSize=200",
            user, user.DefaultTenantId);
        using var response = await fixture.Api.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return page.GetProperty("items").EnumerateArray().ToArray();
    }
}
