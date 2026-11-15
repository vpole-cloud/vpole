using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Postgres.Contracts;
using Pole.Features.Quotas.Domain;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.Outbox.Domain;
using Pole.Features.SeaweedFS.Application.Common;
using Pole.Features.SeaweedFS.Application.Endpoints;
using Pole.Features.SeaweedFS.Application.Lifecycle;
using Pole.Features.SeaweedFS.Domain;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Tenants.Domain;
using Pole.Platform.Hosting;
using Pole.Testing;
using Xunit;

namespace Pole.Features.SeaweedFS.Tests.Integration;

[Collection("seaweedfs-mongo")]
public sealed class SeaweedFSHostTests(
    MongoReplicaSetFixture fixture
) : SeaweedFSHostTestBase(fixture)
{
    [Fact]
    public async Task Tenant_header_is_required_and_downstream_secret_rejection_rolls_back_create()
    {
        await using var host = await StartAsync(services => services.AddScoped<CreateBoundManagedSecret>(_ => (
            _, _,
            _) => Task.FromResult<ManagedSecretMutationOutcome>(new ManagedSecretReferenceRejected())));
        using var client = host.GetClient();
        client.DefaultRequestHeaders.Add(FeatureTestAuthentication.UserHeader, TestPlatformDefaults.UserId);
        var body = await SeedCreateAsync();
        using var missing = await client.PostAsJsonAsync(Route, body, Json);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        client.DefaultRequestHeaders.Add("X-Pole-Tenant-ID", TestPlatformDefaults.TenantId);
        using var rejected = await client.PostAsJsonAsync(Route, body, Json);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        foreach (var collection in new[]
                     { "SeaweedFS", "ResourceDeployments", "ResourceRegistrations", "Secrets", "SecretVersions" })
            Assert.Equal(
                0,
                await Fixture.Database
                    .GetCollection<BsonDocument>(collection)
                    .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        var outbox = await Fixture.Database
            .GetCollection<OutboxMessage>("OutboxMessages")
            .Find(message => message.EventType == "pole.seaweedfs/v1/seaweedfs-service-created")
            .ToListAsync();
        Assert.Empty(outbox);
    }

    [Fact]
    public async Task Bucket_purge_is_retryable_restore_cannot_resurrect_it_and_name_reuse_follows_confirmation()
    {
        var purge = false;
        await using var host = await StartAsync(services => services.AddScoped<PurgeSeaweedFSBucketData>(_ =>
            (_, _) => Task.FromResult(
                purge
                    ? new PurgeSeaweedFSBucketDataOutcome(new SeaweedFSBucketDataPurged())
                    : new PurgeSeaweedFSBucketDataOutcome(
                        new SeaweedFSBucketPurgeRetryableFailure("operator-unavailable")))));
        using var client = AuthenticatedClient(host);
        var body = await SeedCreateAsync();
        using var created = await client.PostAsJsonAsync(Route, body, Json);
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var service = (await created.Content.ReadFromJsonAsync<CreateSeaweedFSServiceResponse>(Json))!;
        var bucketRoute = Route + service.SeaweedFSId + "/buckets";
        using var createdBucket = await client.PostAsJsonAsync(bucketRoute, new { name = "assets" }, Json);
        Assert.True(createdBucket.StatusCode == HttpStatusCode.OK, await createdBucket.Content.ReadAsStringAsync());
        var bucket = (await createdBucket.Content.ReadFromJsonAsync<SeaweedFSBucketDto>(Json))!;
        using var rotated = await client.PostAsJsonAsync(
            bucketRoute + "/" + bucket.Id + "/rotate-credentials",
            new { expectedRevision = 1 },
            Json);
        Assert.True(rotated.StatusCode == HttpStatusCode.OK, await rotated.Content.ReadAsStringAsync());
        using var staleDelete = await client.DeleteAsync(bucketRoute + "/" + bucket.Id + "?expectedRevision=1");
        Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
        using var deleted = await client.DeleteAsync(bucketRoute + "/" + bucket.Id + "?expectedRevision=2");
        Assert.True(deleted.StatusCode == HttpStatusCode.OK, await deleted.Content.ReadAsStringAsync());
        using var readCredentials = await client.GetAsync(bucketRoute + "/" + bucket.Id + "/credentials");
        Assert.Equal(HttpStatusCode.NotFound, readCredentials.StatusCode);
        Clock.Now = Clock.Now.AddSeconds(2);
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SeaweedFSRetentionJob>().ProcessAsync(CancellationToken.None);
        var stored = await Fixture.Database
            .GetCollection<SeaweedFSBucket>("SeaweedFSBuckets")
            .Find(x => x.Id == bucket.Id)
            .SingleAsync();
        Assert.IsType<PurgingSeaweedFSLifecycle>(stored.Lifecycle);
        Assert.True(stored.ReservesName);
        using var restore = await client.PostAsJsonAsync(
            bucketRoute + "/" + bucket.Id + "/restore",
            new { expectedRevision = stored.Revision },
            Json);
        Assert.Equal(HttpStatusCode.Conflict, restore.StatusCode);
        using var duplicate = await client.PostAsJsonAsync(bucketRoute, new { name = "assets" }, Json);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        purge = true;
        await scope.ServiceProvider.GetRequiredService<SeaweedFSRetentionJob>().ProcessAsync(CancellationToken.None);
        stored = await Fixture.Database
            .GetCollection<SeaweedFSBucket>("SeaweedFSBuckets")
            .Find(x => x.Id == bucket.Id)
            .SingleAsync();
        Assert.IsType<DeletedSeaweedFSLifecycle>(stored.Lifecycle);
        Assert.False(stored.ReservesName);
        using var reused = await client.PostAsJsonAsync(bucketRoute, new { name = "assets" }, Json);
        Assert.True(reused.StatusCode == HttpStatusCode.OK, await reused.Content.ReadAsStringAsync());
        var replacement = (await reused.Content.ReadFromJsonAsync<SeaweedFSBucketDto>(Json))!;
        Assert.NotEqual(bucket.Id, replacement.Id);
        Assert.NotEqual(bucket.SecretId, replacement.SecretId);
    }

    [Fact]
    public async Task Retention_processes_all_due_buckets_beyond_a_batch_without_future_rows_or_retries_starving_them()
    {
        var calls = 0;
        await using var host = await StartAsync(services => services.AddScoped<PurgeSeaweedFSBucketData>(_ => (_, _) =>
            Interlocked.Increment(ref calls) == 1
                ? Task.FromException<PurgeSeaweedFSBucketDataOutcome>(
                    new HttpRequestException("provider diagnostic containing sensitive data"))
                : Task.FromResult<PurgeSeaweedFSBucketDataOutcome>(new SeaweedFSBucketDataPurged())));
        using var client = AuthenticatedClient(host);
        using var created = await client.PostAsJsonAsync(Route, await SeedCreateAsync(), Json);
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var service = (await created.Content.ReadFromJsonAsync<CreateSeaweedFSServiceResponse>(Json))!;
        var now = Clock.Now.UtcDateTime;
        var children = new List<SeaweedFSBucket>();
        foreach (var i in Enumerable.Range(0, 202))
            children.Add(
                new SeaweedFSBucket(
                    ObjectId.GenerateNewId().ToString(),
                    TestPlatformDefaults.TenantId,
                    service.SeaweedFSId,
                    "bucket-" + i,
                    ObjectId.GenerateNewId().ToString(),
                    1,
                    new DeletingSeaweedFSLifecycle(now.AddDays(-1), i < 101 ? now.AddDays(7) : now.AddSeconds(-1)),
                    true));
        var buckets = Fixture.Database.GetCollection<SeaweedFSBucket>("SeaweedFSBuckets");
        await buckets.InsertManyAsync(children);
        using var scope = host.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SeaweedFSRetentionJob>().ProcessAsync(CancellationToken.None);
        var stored = await buckets.Find(_ => true).ToListAsync();
        Assert.Equal(101, calls);
        Assert.Equal(101, stored.Count(x => x.Lifecycle is DeletingSeaweedFSLifecycle));
        Assert.Equal(100, stored.Count(x => x.Lifecycle is DeletedSeaweedFSLifecycle));
        Assert.Single(stored, x => x.Lifecycle is PurgingSeaweedFSLifecycle);
    }

    [Fact]
    public async Task
        Existing_metadata_admission_is_atomic_and_cleanup_follows_physical_deletion_before_quota_and_name_release()
    {
        var rejectSecret = true;
        var metadataPurged = false;
        var purgeCalls = 0;
        await using var host = await StartAsync(services =>
        {
            services.AddScoped<ClaimPostgresManagedDatabase>(_ => async (
                request, session,
                ct) =>
            {
                Assert.True(session.IsInTransaction);
                Assert.Equal("filer", request.SourceComponent);
                Assert.Equal(TestPlatformDefaults.DeploymentTargetId, request.Placement.DeploymentTargetId);
                await Fixture.Database
                    .GetCollection<BsonDocument>("TestMetadataClaims")
                    .InsertOneAsync(
                        session,
                        new BsonDocument { ["_id"] = request.Owner.ResourceId },
                        cancellationToken: ct);
                return new PostgresManagedDatabaseReference(
                    ObjectId.GenerateNewId().ToString(),
                    request.PostgresClusterId,
                    "metadata",
                    "metadata_login");
            });
            var originalSecretFactory = services
                .Last(descriptor => descriptor.ServiceType == typeof(CreateBoundManagedSecret))
                .ImplementationFactory!;
            services.AddScoped<CreateBoundManagedSecret>(sp =>
            {
                var original = (CreateBoundManagedSecret)originalSecretFactory(sp);
                return (
                    request, session,
                    ct) => rejectSecret
                    ? Task.FromResult<ManagedSecretMutationOutcome>(new ManagedSecretReferenceRejected())
                    : original(request, session, ct);
            });
            services.AddScoped<PurgePostgresManagedDatabase>(_ => (_, _) =>
            {
                Interlocked.Increment(ref purgeCalls);
                return Task.FromResult(
                    metadataPurged
                        ? new PurgePostgresManagedDatabaseOutcome(new PostgresManagedDatabasePurged())
                        : new PurgePostgresManagedDatabaseOutcome(
                            new PostgresManagedDatabasePending(PostgresManagedDatabasePendingReason.PurgePending)));
            });
        });
        using var client = AuthenticatedClient(host);
        var body = await SeedCreateAsync();
        body = body with
        {
            Spec = body.Spec with
            {
                Mode = SeaweedFSMode.Replicated, ExistingPostgresId = ObjectId.GenerateNewId().ToString()
            }
        };
        using var rejected = await client.PostAsJsonAsync(Route, body, Json);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Equal(
            0,
            await Fixture.Database
                .GetCollection<BsonDocument>("TestMetadataClaims")
                .CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty));
        rejectSecret = false;
        using var created = await client.PostAsJsonAsync(Route, body, Json);
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var response = (await created.Content.ReadFromJsonAsync<CreateSeaweedFSServiceResponse>(Json))!;
        var serviceId = response.SeaweedFSId;
        var services = Fixture.Database.GetCollection<SeaweedFSService>("SeaweedFS");
        var service = await services.Find(x => x.Id == serviceId).SingleAsync();
        Assert.Equal(
            body.Spec.ExistingPostgresId,
            Assert.IsType<ExistingPostgresSeaweedFSMetadata>(service.Spec.Metadata).PostgresId);
        Assert.Equal(new SeaweedFSQuotaCharge(3000, 3072, 12, 18), service.QuotaCharge);
        using var deleted = await client.DeleteAsync(Route + serviceId + "?expectedRevision=1");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Clock.Now = Clock.Now.AddSeconds(2);
        using var scope = host.Services.CreateScope();
        var retention = scope.ServiceProvider.GetRequiredService<SeaweedFSRetentionJob>();
        await retention.ProcessAsync(CancellationToken.None);
        Assert.Equal(0, purgeCalls);
        var deployments = Fixture.Database.GetCollection<ResourceDeployment>("ResourceDeployments");
        var deployment = await deployments.Find(x => x.ResourceId == serviceId).SingleAsync();
        Assert.Equal(ResourceDeploymentOperation.Delete, deployment.DesiredOperation);
        await deployments.ReplaceOneAsync(
            x => x.Id == deployment.Id,
            deployment with
            {
                State = new DeletedResourceDeploymentState(deployment.DesiredGeneration, Clock.Now.UtcDateTime),
                ObservedGeneration = deployment.DesiredGeneration,
                Endpoints = []
            });
        await retention.ProcessAsync(CancellationToken.None);
        Assert.Equal(1, purgeCalls);
        service = await services.Find(x => x.Id == serviceId).SingleAsync();
        Assert.IsType<PurgingSeaweedFSLifecycle>(service.Lifecycle);
        Assert.True(service.ReservesName);
        var usage = await Fixture.Database
            .GetCollection<TenantQuotaUsage>("TenantQuotaUsages")
            .Find(x => x.TenantId == TestPlatformDefaults.TenantId)
            .SingleAsync();
        Assert.Contains(usage.Values, value => value.Key.Value == "CpuMillis" && value.Value == 3000);
        metadataPurged = true;
        await retention.ProcessAsync(CancellationToken.None);
        service = await services.Find(x => x.Id == serviceId).SingleAsync();
        Assert.IsType<DeletedSeaweedFSLifecycle>(service.Lifecycle);
        Assert.False(service.ReservesName);
        usage = await Fixture.Database
            .GetCollection<TenantQuotaUsage>("TenantQuotaUsages")
            .Find(x => x.TenantId == TestPlatformDefaults.TenantId)
            .SingleAsync();
        Assert.Empty(usage.Values);
        using var hidden = await client.GetAsync(Route + serviceId);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using var list = await client.GetAsync(Route);
        Assert.True(list.StatusCode == HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        var page = (await list.Content.ReadFromJsonAsync<ListSeaweedFSOutcome>(Json))!;
        Assert.Empty(page.Items);
        using var reused = await client.PostAsJsonAsync(Route, body, Json);
        Assert.True(reused.StatusCode == HttpStatusCode.Accepted, await reused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Admin_credentials_require_current_owner_membership_and_reveals_are_audited_without_values()
    {
        await using var host = await StartAsync();
        using var client = AuthenticatedClient(host);
        using var created = await client.PostAsJsonAsync(Route, await SeedCreateAsync(), Json);
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var service = (await created.Content.ReadFromJsonAsync<CreateSeaweedFSServiceResponse>(Json))!;
        var credentialsRoute = Route + service.SeaweedFSId + "/credentials";
        using var revealed = await client.GetAsync(credentialsRoute);
        Assert.True(revealed.StatusCode == HttpStatusCode.OK, await revealed.Content.ReadAsStringAsync());
        Assert.True(revealed.Headers.CacheControl?.NoStore);
        var credentials = (await revealed.Content.ReadFromJsonAsync<SeaweedFSCredentialsDto>(Json))!;
        Assert.False(string.IsNullOrWhiteSpace(credentials.AccessKey));
        var audits = await Fixture.Database
            .GetCollection<OutboxMessage>("OutboxMessages")
            .Find(x => x.EventType == "pole.seaweedfs/v1/credentials-revealed")
            .ToListAsync();
        var audit = Assert.Single(audits);
        Assert.Contains(TestPlatformDefaults.UserId, audit.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.AccessKey, audit.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(credentials.SecretKey, audit.PayloadJson, StringComparison.Ordinal);
        var tenants = Fixture.Database.GetCollection<Tenant>("Tenants");
        var tenant = await tenants.Find(x => x.Id == TestPlatformDefaults.TenantId).SingleAsync();
        await tenants.ReplaceOneAsync(
            x => x.Id == tenant.Id,
            tenant with
            {
                Members = tenant.Members.Select(member => member with { Role = TenantRole.Admin }).ToImmutableList()
            });
        using var deniedAdmin = await client.GetAsync(credentialsRoute);
        Assert.Equal(HttpStatusCode.Forbidden, deniedAdmin.StatusCode);
        await tenants.ReplaceOneAsync(
            x => x.Id == tenant.Id,
            tenant with
            {
                Members = tenant.Members.Select(member => member with { Role = TenantRole.Member }).ToImmutableList()
            });
        using var deniedMember = await client.GetAsync(credentialsRoute);
        Assert.Equal(HttpStatusCode.Forbidden, deniedMember.StatusCode);
        using var bucketCreated = await client.PostAsJsonAsync(
            Route + service.SeaweedFSId + "/buckets",
            new { name = "assets" },
            Json);
        Assert.True(bucketCreated.StatusCode == HttpStatusCode.OK, await bucketCreated.Content.ReadAsStringAsync());
        var bucket = (await bucketCreated.Content.ReadFromJsonAsync<SeaweedFSBucketDto>(Json))!;
        using var bucketCredentials
            = await client.GetAsync(Route + service.SeaweedFSId + "/buckets/" + bucket.Id + "/credentials");
        Assert.True(
            bucketCredentials.StatusCode == HttpStatusCode.OK,
            await bucketCredentials.Content.ReadAsStringAsync());
    }
}
