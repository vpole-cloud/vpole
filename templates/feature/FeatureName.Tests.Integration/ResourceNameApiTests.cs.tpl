// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.ASP.Helpers;
using Pole.Features.Core.Multitenancy;
using Pole.Features.FeatureName.Application;
using Pole.Features.FeatureName.Application.Common;
using Pole.Features.FeatureName.Application.Creation;
using Pole.Features.FeatureName.Application.Updates;
using Pole.Features.FeatureName.Domain;
using Pole.Features.FeatureName.Events;
using Pole.Features.FeatureName.Migrations;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Outbox.Domain;
using Pole.Features.ResourceConnections.Application;
using Pole.Platform.Hosting;
using Pole.Testing;
using Xunit;

namespace Pole.Features.FeatureName.Tests.Integration;

[CollectionDefinition("feature-route-mongo")]
public sealed class FeatureNameMongoCollection : ICollectionFixture<MongoReplicaSetFixture>;

[Collection("feature-route-mongo")]
public sealed class ResourceNameApiTests(
    MongoReplicaSetFixture fixture
) : IAsyncLifetime
{
    private const string Route = "/api/v1/feature-route/";
    private static readonly JsonSerializerOptions JsonOptions = PoleHttpJson.CreateSerializerOptions();

    private IMongoCollection<ResourceName> Resources =>
        fixture.Database.GetCollection<ResourceName>("FeatureName");

    private IMongoCollection<OutboxMessage> Outbox => fixture.Database.GetCollection<OutboxMessage>("OutboxMessages");

    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Create_get_and_outbox_use_the_actual_registered_feature_and_schema()
    {
        await using var host = await StartAsync();
        using var client = AuthenticatedClient(host, TestPlatformDefaults.TenantId);
        using var created = await client.PostAsJsonAsync(
            Route,
            new ResourceNameDetailsDto(" Alpha ", " Initial "),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = Assert.IsType<ResourceNameDto>(
            await created.Content.ReadFromJsonAsync<ResourceNameDto>(JsonOptions));
        Assert.Equal("Alpha", dto.Details.Name);
        Assert.Equal(1, dto.Revision);
        using var read = await client.GetAsync(created.Headers.Location);
        Assert.Equal(dto, await read.Content.ReadFromJsonAsync<ResourceNameDto>(JsonOptions));
        var stored = await Resources.Find(item => item.Id == dto.Id).SingleAsync();
        var message = await Outbox.Find(item => item.EventType == "pole.feature-route/v1/resource-created")
            .SingleAsync();
        Assert.Equal(stored.TenantId, message.TenantId);
        var changed = Assert.IsType<ResourceNameCreatedEvent>(
            JsonSerializer.Deserialize<ResourceNameCreatedEvent>(
                message.PayloadJson,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(dto.Id, changed.ResourceId);
        Assert.Equal(stored.TenantId, changed.EventCore.TenantId);
    }

    [Fact]
    public async Task Concurrent_duplicate_names_commit_exactly_one_resource_and_event()
    {
        await using var host = await StartAsync();
        using var client = AuthenticatedClient(host, TestPlatformDefaults.TenantId);
        var body = new ResourceNameDetailsDto("Alpha", "Initial");
        var replies = await Task.WhenAll(
            client.PostAsJsonAsync(Route, body, JsonOptions),
            client.PostAsJsonAsync(Route, body, JsonOptions));
        try
        {
            Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.Created);
            var conflict = Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.Conflict);
            await AssertCodeAsync(conflict, "feature-route.name-conflict");
            Assert.Equal(1, await Resources.CountDocumentsAsync(Builders<ResourceName>.Filter.Empty));
            Assert.Equal(1, await Outbox.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
        }
        finally
        {
            foreach (var reply in replies)
                reply.Dispose();
        }
    }

    [Fact]
    public async Task Both_tenants_have_access_but_cannot_read_or_update_each_others_resources()
    {
        await using var host = await StartAsync();
        var otherTenant = ObjectId.GenerateNewId().ToString();
        await host.SeedTenantAsync(otherTenant);
        using var first = AuthenticatedClient(host, TestPlatformDefaults.TenantId);
        using var second = AuthenticatedClient(host, otherTenant);
        var dto = await CreateAsync(first);
        using var hidden = await second.GetAsync(Route + dto.Id);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var refused = await second.PutAsJsonAsync(
            Route + dto.Id,
            new { expectedRevision = 1, details = new ResourceNameDetailsDto("Beta", "Changed") },
            JsonOptions);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        using var sameName = await second.PostAsJsonAsync(Route, dto.Details, JsonOptions);
        Assert.Equal(HttpStatusCode.Created, sameName.StatusCode);
        Assert.Equal(2, await Resources.CountDocumentsAsync(Builders<ResourceName>.Filter.Empty));
        Assert.Equal(2, await Outbox.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
        Assert.Equal("Alpha", (await Resources.Find(item => item.Id == dto.Id).SingleAsync()).Details.Name);
    }

    [Fact]
    public async Task Update_requires_expected_revision_and_normalized_no_op_has_no_side_effects()
    {
        await using var host = await StartAsync();
        using var client = AuthenticatedClient(host, TestPlatformDefaults.TenantId);
        var dto = await CreateAsync(client);
        using var updated = await client.PutAsJsonAsync(
            Route + dto.Id,
            new { expectedRevision = 1, details = new ResourceNameDetailsDto("Beta", "Changed") },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var current = await Resources.Find(item => item.Id == dto.Id).SingleAsync();
        Assert.Equal(2, current.Revision);
        using var stale = await client.PutAsJsonAsync(
            Route + dto.Id,
            new { expectedRevision = 1, details = new ResourceNameDetailsDto("Gamma", "Other") },
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await AssertCodeAsync(stale, "feature-route.revision-conflict");
        using var unchanged = await client.PutAsJsonAsync(
            Route + dto.Id,
            new { expectedRevision = 2, details = new ResourceNameDetailsDto(" Beta ", " Changed ") },
            JsonOptions);
        Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
        Assert.Equal(current, await Resources.Find(item => item.Id == dto.Id).SingleAsync());
        Assert.Equal(2, await Outbox.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
    }

    [Fact]
    public async Task Concurrent_updates_with_one_expected_revision_have_one_winner()
    {
        await using var host = await StartAsync();
        using var client = AuthenticatedClient(host, TestPlatformDefaults.TenantId);
        var dto = await CreateAsync(client);
        var replies = await Task.WhenAll(
            client.PutAsJsonAsync(
                Route + dto.Id,
                new { expectedRevision = 1, details = new ResourceNameDetailsDto("Beta", "First") },
                JsonOptions),
            client.PutAsJsonAsync(
                Route + dto.Id,
                new { expectedRevision = 1, details = new ResourceNameDetailsDto("Gamma", "Second") },
                JsonOptions));
        try
        {
            Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.OK);
            var conflict = Assert.Single(replies, reply => reply.StatusCode == HttpStatusCode.Conflict);
            await AssertCodeAsync(conflict, "feature-route.revision-conflict");
            Assert.Equal(2, (await Resources.Find(item => item.Id == dto.Id).SingleAsync()).Revision);
            Assert.Equal(2, await Outbox.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
        }
        finally
        {
            foreach (var reply in replies)
                reply.Dispose();
        }
    }

    [Fact]
    public async Task Rename_conflict_does_not_change_the_existing_resource_or_add_an_event()
    {
        await using var host = await StartAsync();
        using var client = AuthenticatedClient(host, TestPlatformDefaults.TenantId);
        var dto = await CreateAsync(client);
        using var second = await client.PostAsJsonAsync(
            Route,
            new ResourceNameDetailsDto("Beta", "Other"),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var original = await Resources.Find(item => item.Id == dto.Id).SingleAsync();
        using var conflict = await client.PutAsJsonAsync(
            Route + dto.Id,
            new { expectedRevision = 1, details = new ResourceNameDetailsDto("Beta", "Changed") },
            JsonOptions);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await AssertCodeAsync(conflict, "feature-route.name-conflict");
        Assert.Equal(original, await Resources.Find(item => item.Id == dto.Id).SingleAsync());
        Assert.Equal(2, await Outbox.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
    }

    [Fact]
    public async Task Actual_migration_rejects_an_invalid_stored_revision()
    {
        await using var host = await StartAsync();
        var resource = ResourceNameFactory.Create(
            TestPlatformDefaults.TenantId,
            ResourceNameDetails.Create("Alpha", "Initial"),
            new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));
        var failure = await Assert.ThrowsAsync<MongoWriteException>(() =>
            Resources.InsertOneAsync(resource with { Revision = 0 }));
        Assert.Equal(121, failure.WriteError.Code);
        Assert.Equal(0, await Resources.CountDocumentsAsync(Builders<ResourceName>.Filter.Empty));
    }

    [Fact]
    public async Task Invalid_input_and_missing_authority_do_not_reach_mutation()
    {
        await using var host = await StartAsync();
        using var anonymous = host.GetClient();
        using var denied = await anonymous.PostAsJsonAsync(
            Route,
            new ResourceNameDetailsDto("Alpha", ""),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var withoutTenant = AuthenticatedClient(host, tenantId: null);
        using var missing = await withoutTenant.GetAsync(Route + ObjectId.GenerateNewId());
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var client = AuthenticatedClient(host, TestPlatformDefaults.TenantId);
        using var invalid = await client.PostAsJsonAsync(Route, new ResourceNameDetailsDto("", ""), JsonOptions);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(0, await Resources.CountDocumentsAsync(Builders<ResourceName>.Filter.Empty));
        Assert.Equal(0, await Outbox.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_after_actual_outbox_insert_rolls_back_root_and_outbox(bool update)
    {
        await using var host = await StartAsync(failOutbox: true);
        var resource = ResourceNameFactory.Create(
            TestPlatformDefaults.TenantId,
            ResourceNameDetails.Create("Alpha", "Initial"),
            new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow));
        if (update)
        {
            await Resources.InsertOneAsync(resource);
            resource = await Resources.Find(item => item.Id == resource.Id).SingleAsync();
        }

        await using var scope = host.Services.CreateAsyncScope();
        if (update)
        {
            var persist = scope.ServiceProvider.GetRequiredService<PersistUpdatedResourceName>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => persist(
                new(resource.TenantId, resource.Id, 1, ResourceNameDetails.Create("Beta", "Changed")),
                CancellationToken.None));
            Assert.Equal(resource, await Resources.Find(item => item.Id == resource.Id).SingleAsync());
        }
        else
        {
            var persist = scope.ServiceProvider.GetRequiredService<PersistCreatedResourceName>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => persist(resource, CancellationToken.None));
            Assert.Equal(0, await Resources.CountDocumentsAsync(Builders<ResourceName>.Filter.Empty));
        }

        Assert.Equal(0, await Outbox.CountDocumentsAsync(Builders<OutboxMessage>.Filter.Empty));
    }

    private async Task<FeatureTestHost> StartAsync(bool failOutbox = false)
    {
        var host = await FeatureTestHost.StartAsync(
            new ConfigurationBuilder().Build(),
            typeof(FeatureNameFeature).Assembly,
            typeof(ResourceNameCreatedEvent).Assembly,
            (services, _) => services.AddFeatureNameFeature().AddResourceConnectionsFeature(),
            endpoints => endpoints.MapFeatureNameEndpoints(),
            configureServices: services =>
            {
                services.AddSingleton(fixture.Client);
                services.AddSingleton(fixture.Database);
                services.AddFeatureMigrations(
                    "pole.feature-route",
                    typeof(FeatureNameSchemaMigration).Assembly);

                if (failOutbox)
                {
                    services.RemoveAll<StoreOutboxWrites>();
                    services.AddSingleton<StoreOutboxWrites>(_ => async (
                        session, writes,
                        ct) =>
                    {
                        await fixture.StoreOutboxWrites(session, writes, ct);
                        throw new InvalidOperationException("Injected failure after outbox insert.");
                    });
                }
            });
        await host.SeedTenantAsync();
        return host;
    }

    private static HttpClient AuthenticatedClient(FeatureTestHost host, string? tenantId)
    {
        var client = host.GetClient();
        client.DefaultRequestHeaders.Add(FeatureTestAuthentication.UserHeader, TestPlatformDefaults.UserId);
        if (tenantId is not null)
            client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, tenantId);
        return client;
    }

    private static async Task<ResourceNameDto> CreateAsync(HttpClient client)
    {
        using var created = await client.PostAsJsonAsync(
            Route,
            new ResourceNameDetailsDto("Alpha", "Initial"),
            JsonOptions);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        return Assert.IsType<ResourceNameDto>(await created.Content.ReadFromJsonAsync<ResourceNameDto>(JsonOptions));
    }

    private static async Task AssertCodeAsync(HttpResponseMessage response, string expected)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        Assert.Equal(expected, body.GetProperty("code").GetString());
    }
}
