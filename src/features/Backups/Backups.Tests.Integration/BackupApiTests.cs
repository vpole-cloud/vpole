// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Backups.Application;
using Pole.Features.Backups.Application.Archives;
using Pole.Features.Backups.Application.Configuration;
using Pole.Features.Backups.Application.Consumers;
using Pole.Features.Backups.Application.Destinations;
using Pole.Features.Backups.Application.Endpoints.Operations;
using Pole.Features.Backups.Application.Jobs;
using Pole.Features.Backups.Application.Operations;
using Pole.Features.Backups.Application.Reconciliation;
using Pole.Features.Backups.Contracts;
using Pole.Features.Backups.Domain;
using Pole.Features.Backups.Events;
using Pole.Features.Backups.Migrations;
using Pole.Features.ComputeProfiles.Domain;
using Pole.Features.Core.Multitenancy;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Outbox.Domain;
using Pole.Features.ResourceConnections.Application;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Domain;
using Pole.Features.ResourceGroups.Application;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.ResourceGroups.Events;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Primitives;
using Pole.Features.StorageProfiles.Domain;
using Pole.Features.StorageProfiles.Primitives;
using Pole.Features.Tenants.Domain;
using Pole.Observability;
using Pole.Platform.Hosting;
using Pole.Testing;
using Pole.Workloads;
using Xunit;
using EventExtensions = Pole.Features.Events.Abstractions.EventExtensions;

namespace Pole.Features.Backups.Tests.Integration;

[CollectionDefinition("backup-mongo")]
public sealed class BackupMongoCollection : ICollectionFixture<MongoReplicaSetFixture>;

[Collection("backup-mongo")]
public sealed class BackupApiTests(
    MongoReplicaSetFixture mongo
) : IAsyncLifetime
{
    private const string Route = "/api/v1/backups";
    private readonly BackupTestSetup setup = new(mongo);
    public Task InitializeAsync() => mongo.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Policy_consumer_preserves_origin_and_schedules_durable_continuation()
    {
        const string tenant = TestPlatformDefaults.TenantId;
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, tenant);
        var destinationInput = await BackupTestSetup.StorageAsync(client);
        using var destinationResponse = await client.PostAsJsonAsync(Route + "/destinations", destinationInput);
        destinationResponse.EnsureSuccessStatusCode();
        var destination = (await destinationResponse.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        await using var scope = host.Services.CreateAsyncScope();
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var sourceId = ObjectId.GenerateNewId().ToString();
        await setup.SeedSourceAsync(
            scope.ServiceProvider,
            destinationInput,
            ResourceTypeId.Create(provider.ResourceType),
            sourceId,
            ObjectId.GenerateNewId().ToString());
        using var response = await client.PostAsJsonAsync(
            Route + "/policies",
            new BackupPolicyInput("daily", provider.ResourceType, sourceId, destination.Id, "0 2 * * *", false, 30));
        response.EnsureSuccessStatusCode();
        var policy = (await response.Content.ReadFromJsonAsync<BackupPolicyDto>())!;
        var outbox = mongo.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var row = await outbox.Find(message => message.EventType == "pole.backups/v1/policy-work-requested")
            .SingleAsync();
        var requested = JsonSerializer.Deserialize<BackupPolicyWorkRequestedEvent>(
            row.PayloadJson,
            EventExtensions.SerializerOptions)!;
        Assert.Equal(policy.Id, requested.PolicyId);
        Assert.Equal(tenant, requested.EventCore.TenantId);
        Assert.True(requested.NotBeforeUtc <= DateTime.UtcNow);
        Assert.Equal(tenant, row.TenantId);
        var policies = mongo.Database.GetCollection<BackupPolicy>("BackupPolicies");
        var stored = await policies.Find(value => value.Id == policy.Id).SingleAsync();
        var origin = new TraceContextSnapshot(stored.Dispatch.Origin.TraceId, stored.Dispatch.Origin.CorrelationId);
        Assert.Equal(origin.TraceId, row.TraceId);
        Assert.Equal(origin.CorrelationId, row.CorrelationId);

        using (TraceContext.Push(TraceContext.Create()))
            await SendToConsumerAsync<ApplyRequestedBackupPolicy, BackupPolicyWorkRequestedEvent>(
                scope.ServiceProvider,
                requested);
        await WaitForAsync(async () =>
            (await policies.Find(value => value.Id == policy.Id).SingleAsync()).AppliedRevision == policy.Revision);
        Assert.Equal(origin, provider.LastApplyTrace);
        Assert.Equal(1, provider.ApplyCount);
        Assert.False(
            await scope.ServiceProvider
                .GetRequiredService<ApplyBackupPolicy>()
                .ExecuteAsync(tenant, policy.Id, default));

        var continuationRows = await outbox.Find(message => message.EventType == row.EventType).ToListAsync();
        var continuation = continuationRows
            .Select(message => JsonSerializer.Deserialize<BackupPolicyWorkRequestedEvent>(
                message.PayloadJson,
                EventExtensions.SerializerOptions)!)
            .Single(message => message.NotBeforeUtc > requested.NotBeforeUtc);
        Assert.Equal(origin.TraceId, continuation.EventCore.TraceId);
        Assert.Equal(origin.CorrelationId, continuation.EventCore.CorrelationId);
        await SendToConsumerAsync<ApplyRequestedBackupPolicy, BackupPolicyWorkRequestedEvent>(
            scope.ServiceProvider,
            continuation);
        var jobs = host.Services.GetRequiredService<RecordedBackgroundJobs>();
        await WaitForAsync(() =>
            Task.FromResult(jobs.Jobs.Values.Any(item => item.Job.Type == typeof(BackupPolicyJob))));
        var job = Assert.Single(jobs.Jobs.Values, item => item.Job.Type == typeof(BackupPolicyJob));
        Assert.Equal(tenant, job.Job.Args[0]);
        Assert.Equal(policy.Id, job.Job.Args[1]);
        Assert.Equal(1, provider.ApplyCount);

        using var runResponse = await client.PostAsJsonAsync(
            Route + "/policies/" + policy.Id + "/runs",
            new { idempotencyKey = "manual-once" });
        runResponse.EnsureSuccessStatusCode();
        var operation = (await runResponse.Content.ReadFromJsonAsync<BackupOperationDto>())!;
        var operationRow = await outbox.Find(message => message.EventType == "pole.backups/v1/operation-work-requested")
            .SingleAsync();
        var operationRequest = JsonSerializer.Deserialize<BackupOperationWorkRequestedEvent>(
            operationRow.PayloadJson,
            EventExtensions.SerializerOptions)!;
        await SendToConsumerAsync<ExecuteRequestedBackupOperation, BackupOperationWorkRequestedEvent>(
            scope.ServiceProvider,
            operationRequest);
        var operations = mongo.Database.GetCollection<BackupOperation>("BackupOperations");
        await WaitForAsync(async () => (await operations.Find(value => value.Id == operation.Id).SingleAsync()).State ==
            BackupOperationState.Succeeded);
        var completed = await operations.Find(value => value.Id == operation.Id).SingleAsync();
        Assert.Equal(
            new TraceContextSnapshot(completed.Origin.TraceId, completed.Origin.CorrelationId),
            provider.LastRunTrace);
        Assert.Equal(1, provider.RunCount);
        Assert.False(
            await scope.ServiceProvider
                .GetRequiredService<ExecuteBackupOperation>()
                .ExecuteAsync(tenant, operation.Id, default));
        Assert.Equal(1, provider.RunCount);
    }

    [Fact]
    public async Task Recovery_requeues_a_bounded_batch_of_lost_or_expired_work_with_original_trace()
    {
        await using var host = await setup.StartAsync();
        await using var scope = host.Services.CreateAsyncScope();
        var origin = new BackupExecutionOrigin("1234567890abcdef1234567890abcdef", "lost-enqueue");
        var now = DateTime.UtcNow.AddMinutes(-10);
        var pending = Enumerable.Range(0, 125)
            .Select(index => BackupOperation.RequestCopy(
                ObjectId.GenerateNewId().ToString(),
                TestPlatformDefaults.TenantId,
                "recovery-" + index,
                ObjectId.GenerateNewId().ToString(),
                ObjectId.GenerateNewId().ToString(),
                now.AddSeconds(index),
                origin))
            .ToArray();
        pending[0] = pending[0].Claim(new BackupLease("expired", now.AddMinutes(1)));
        var future = BackupOperation.RequestCopy(
            ObjectId.GenerateNewId().ToString(),
            TestPlatformDefaults.TenantId,
            "future",
            pending[0].PolicyId,
            pending[0].ArchiveId,
            DateTime.UtcNow.AddHours(1),
            origin);
        var leased = BackupOperation
            .RequestCopy(
                ObjectId.GenerateNewId().ToString(),
                TestPlatformDefaults.TenantId,
                "leased",
                pending[0].PolicyId,
                pending[0].ArchiveId,
                now,
                origin)
            .Claim(new BackupLease("live", DateTime.UtcNow.AddMinutes(2)));
        var operations = mongo.Database.GetCollection<BackupOperation>("BackupOperations");
        await operations.InsertManyAsync([.. pending, future, leased]);
        await scope.ServiceProvider.GetRequiredService<BackupRecoveryScanner>().RequestDueWorkAsync(default);
        var outbox = mongo.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var rows = await outbox.Find(message => message.EventType == "pole.backups/v1/operation-work-requested")
            .ToListAsync();
        Assert.Equal(100, rows.Count);
        var requests = rows.Select(row =>
                JsonSerializer.Deserialize<BackupOperationWorkRequestedEvent>(
                    row.PayloadJson,
                    EventExtensions.SerializerOptions)!)
            .ToArray();
        Assert.Contains(requests, request => request.OperationId == pending[0].Id);
        Assert.DoesNotContain(
            requests,
            request => request.OperationId == future.Id || request.OperationId == leased.Id);
        Assert.All(
            requests,
            request =>
            {
                Assert.Equal(TestPlatformDefaults.TenantId, request.EventCore.TenantId);
                Assert.Equal(origin.TraceId, request.EventCore.TraceId);
                Assert.Equal(origin.CorrelationId, request.EventCore.CorrelationId);
            });
    }

    [Fact]
    public async Task Operation_and_both_outbox_events_roll_back_if_work_request_write_fails()
    {
        const string tenant = TestPlatformDefaults.TenantId;
        await using var host = await setup.StartAsync(failOperation: true);
        using var client = BackupTestSetup.Client(host, tenant);
        var input = await BackupTestSetup.StorageAsync(client);
        using var destinationResponse = await client.PostAsJsonAsync(Route + "/destinations", input);
        destinationResponse.EnsureSuccessStatusCode();
        var destination = (await destinationResponse.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        await using var scope = host.Services.CreateAsyncScope();
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var sourceId = ObjectId.GenerateNewId().ToString();
        await setup.SeedSourceAsync(
            scope.ServiceProvider,
            input,
            ResourceTypeId.Create(provider.ResourceType),
            sourceId,
            ObjectId.GenerateNewId().ToString());
        using var policyResponse = await client.PostAsJsonAsync(
            Route + "/policies",
            new BackupPolicyInput("daily", provider.ResourceType, sourceId, destination.Id, "0 2 * * *", false, 30));
        policyResponse.EnsureSuccessStatusCode();
        var policy = (await policyResponse.Content.ReadFromJsonAsync<BackupPolicyDto>())!;
        var outbox = mongo.Database.GetCollection<OutboxMessage>("OutboxMessages");
        var baseline = await outbox.CountDocumentsAsync(_ => true);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.ServiceProvider
                .GetRequiredService<RequestManualBackup>()
                .ExecuteAsync(new ManualBackupCommand(tenant, "rolled-back", policy.Id), default));
        Assert.Equal(
            0,
            await mongo.Database.GetCollection<BackupOperation>("BackupOperations").CountDocumentsAsync(_ => true));
        Assert.Equal(baseline, await outbox.CountDocumentsAsync(_ => true));
        var archive = await mongo.Database
            .GetCollection<BackupArchive>("BackupArchives")
            .Find(value => value.Id == policy.ArchiveId)
            .SingleAsync();
        Assert.Equal(0, archive.ReferenceFence);
        Assert.Equal(0, provider.RunCount);
    }

    // Exercise consumer dispatch in the in-memory host. RabbitMQ's publish topology for the
    // versioned entity names is a separate production-composition boundary covered by Aspire E2E.
    private static async Task SendToConsumerAsync<TConsumer, TMessage>(IServiceProvider services, TMessage message)
        where TConsumer : class, IConsumer<TMessage>
        where TMessage : class
    {
        var address = new Uri("queue:" + DefaultEndpointNameFormatter.Instance.Consumer<TConsumer>());
        var endpoint = await services.GetRequiredService<ISendEndpointProvider>().GetSendEndpoint(address);
        await endpoint.Send(message);
    }

    private static async Task WaitForAsync(Func<Task<bool>> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!await ready())
            await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
    }

    [Fact]
    public async Task History_pages_are_bounded_tenant_scoped_and_stable_when_timestamps_match()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var timestamp = new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);
        var rows = Enumerable.Range(0, 53)
            .Select(index => BackupOperation.RequestCopy(
                ObjectId.GenerateNewId().ToString(),
                TestPlatformDefaults.TenantId,
                $"history-{index}",
                ObjectId.GenerateNewId().ToString(),
                ObjectId.GenerateNewId().ToString(),
                timestamp,
                new BackupExecutionOrigin("trace", "correlation")))
            .ToArray();
        var otherTenant = ObjectId.GenerateNewId().ToString();
        await host.SeedTenantAsync(otherTenant);
        var foreign = BackupOperation.RequestCopy(
            ObjectId.GenerateNewId().ToString(),
            otherTenant,
            "foreign",
            ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            timestamp.AddDays(1),
            new BackupExecutionOrigin("trace", "correlation"));
        var operations = mongo.Database.GetCollection<BackupOperation>("BackupOperations");
        await operations.InsertManyAsync([.. rows, foreign]);

        var initial = (await client.GetFromJsonAsync<BackupOperationPage>(Route + "/operations"))!;
        Assert.Equal(50, initial.Items.Length);
        Assert.NotNull(initial.NextCursor);
        var seen = initial.Items.Select(item => item.Id).ToList();
        var newer = BackupOperation.RequestCopy(
            ObjectId.GenerateNewId().ToString(),
            TestPlatformDefaults.TenantId,
            "newer",
            rows[0].PolicyId,
            rows[0].ArchiveId,
            timestamp.AddSeconds(1),
            new BackupExecutionOrigin("trace", "correlation"));
        await operations.InsertOneAsync(newer);
        var next = (await client.GetFromJsonAsync<BackupOperationPage>(
            Route + "/operations?after=" + Uri.EscapeDataString(initial.NextCursor)))!;
        Assert.Equal(3, next.Items.Length);
        Assert.Null(next.NextCursor);
        seen.AddRange(next.Items.Select(item => item.Id));
        Assert.Equal(rows.Select(item => item.Id).OrderDescending(), seen);
        Assert.Equal(53, seen.Distinct().Count());
        var small = (await client.GetFromJsonAsync<BackupOperationPage>(Route + "/operations?pageSize=2"))!;
        Assert.Equal(2, small.Items.Length);
        Assert.Equal(newer.Id, small.Items[0].Id);

        using var other = BackupTestSetup.Client(host, otherTenant);
        Assert.Equal(
            foreign.Id,
            Assert.Single((await other.GetFromJsonAsync<BackupOperationPage>(Route + "/operations"))!.Items).Id);
        Assert.Empty(
            (await other.GetFromJsonAsync<BackupOperationPage>(
                Route + "/operations?after=" + Uri.EscapeDataString(initial.NextCursor)))!.Items);
        foreach (var query in new[] { "after=invalid", "pageSize=201" })
        {
            using var rejected = await client.GetAsync(Route + "/operations?" + query);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }
    }

    [Fact]
    public async Task A_second_registered_consumer_reads_only_its_granted_keys_without_a_live_deployment()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var input = await BackupTestSetup.StorageAsync(client);
        var type = ResourceTypeId.Create("tests/archive-reader");
        var consumerId = ObjectId.GenerateNewId().ToString();
        await using var scope = host.Services.CreateAsyncScope();
        var access = new ConsumerSecretAccessRequest(
            TestPlatformDefaults.TenantId,
            input.ResourceGroupId,
            input.IsolationScopeId,
            type,
            consumerId,
            input.CredentialSecretId,
            [input.AccessKeyIdKey]);
        await scope.ServiceProvider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<bool>(async (tx, ct) =>
            {
                Assert.IsType<RegisteredResource>(
                    (await scope.ServiceProvider
                        .CreatePluginResourceRegistrar("tests")
                        .RegisterAsync(
                            new(
                                TestPlatformDefaults.TenantId,
                                input.ResourceGroupId,
                                new ResourceRegistrationDescriptor(type, consumerId, "independent-reader")),
                            tx.Session,
                            ct)).Value);
                var granted = await scope.ServiceProvider.GetRequiredService<GrantConsumerSecretAccess>()(
                    access,
                    tx.Session,
                    ct);
                Assert.IsType<ConsumerSecretAccessGranted>(granted.Value);
                return tx.Commit(true);
            });

        var read = scope.ServiceProvider.GetRequiredService<ReadConsumerSecretValues>();
        var selected = Assert.IsType<ConsumerSecretValues>((await read(access, default)).Value);
        Assert.Equal(input.AccessKeyIdKey, Assert.Single(selected.Values).Key);
        Assert.DoesNotContain(selected.Values.Values.First(), selected.ToString());
        Assert.IsType<ConsumerSecretAccessDenied>(
            (await read(access with { Keys = [input.SecretAccessKeyKey] }, default)).Value);
        Assert.IsType<ConsumerSecretAccessDenied>(
            (await read(access with { ConsumerType = BackupResourceTypes.Destination }, default)).Value);
    }

    [Fact]
    public async Task Restore_validation_identifies_ambiguous_missing_and_unzoned_targets()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var route = Route + "/archives/" + ObjectId.GenerateNewId() + "/restores";
        foreach (var (backupId, targetTime, field) in new (string?, string?, string)[]
                 {
                     (null, null, "Body.BackupId"),
                     ("copy", "2026-11-13T00:00:00Z", "Body.TargetTime"),
                     (null, "2026-11-13T00:00:00", "Body.TargetTime"),
                 })
        {
            using var response = await client.PostAsJsonAsync(
                route,
                new { idempotencyKey = "invalid-target", name = "restore", backupId, targetTime, confirmed = true });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        }
    }

    [Fact]
    public async Task Destination_validation_reports_the_invalid_field_without_writing_configuration()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var input = await BackupTestSetup.StorageAsync(client);

        foreach (var (body, field) in new[]
                 {
                     (input with { Endpoint = "https://user:password@example.test" }, "Body.Endpoint"),
                     (input with { Bucket = "INVALID" }, "Body.Bucket"),
                     (input with { Prefix = "../escape" }, "Body.Prefix"),
                     (input with { SecretAccessKeyKey = input.AccessKeyIdKey }, "Body.SecretAccessKeyKey"),
                 })
        {
            using var response = await client.PostAsJsonAsync(Route + "/destinations", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.True(problem.RootElement.GetProperty("errors").TryGetProperty(field, out _));
        }

        Assert.Equal(
            0,
            await mongo.Database.GetCollection<BackupDestination>("BackupDestinations").CountDocumentsAsync(_ => true));
    }

    [Fact]
    public async Task Destination_and_grant_commit_in_same_scope_and_remain_invisible_to_other_tenant()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var input = await BackupTestSetup.StorageAsync(client);
        using var created = await client.PostAsJsonAsync(Route + "/destinations", input);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = await created.Content.ReadFromJsonAsync<BackupDestinationDto>();
        Assert.NotNull(item);
        using var read = await client.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.DoesNotContain("catalog-private-value", await read.Content.ReadAsStringAsync());
        var grants = host.Services.CreateAsyncScope();
        await using (grants)
        {
            var use = grants.ServiceProvider.GetRequiredService<ReadConsumerSecretValues>();
            var access = new ConsumerSecretAccessRequest(
                TestPlatformDefaults.TenantId,
                input.ResourceGroupId,
                input.IsolationScopeId,
                BackupResourceTypes.Destination,
                item.Id,
                input.CredentialSecretId,
                [input.AccessKeyIdKey, input.SecretAccessKeyKey]);
            Assert.IsType<ConsumerSecretValues>((await use(access, default)).Value);
            using var rotated = await client.PostAsJsonAsync(
                "/api/v1/secrets/" + input.CredentialSecretId + "/versions",
                new { expectedVersion = 1, values = new { id = "rotated-id", key = "rotated-test-key" } });
            rotated.EnsureSuccessStatusCode();
            var newKeys = Assert.IsType<ConsumerSecretValues>((await use(access, default)).Value);
            Assert.Equal("rotated-id", newKeys.Values[input.AccessKeyIdKey]);
            Assert.IsType<ConsumerSecretAccessDenied>(
                (await use(access with { ConsumerId = ObjectId.GenerateNewId().ToString() }, default)).Value);
        }

        var other = ObjectId.GenerateNewId().ToString();
        await host.SeedTenantAsync(other);
        using var foreign = BackupTestSetup.Client(host, other);
        using var hidden = await foreign.GetAsync(created.Headers.Location);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        var list = await foreign.GetFromJsonAsync<BackupDestinationDto[]>(Route + "/destinations");
        Assert.Empty(list!);
    }

    [Fact]
    public async Task Restore_and_forget_require_an_administrator_of_the_selected_tenant()
    {
        await using var host = await setup.StartAsync();
        var tenant = TestPlatformDefaults.TenantId;
        var member = ObjectId.GenerateNewId().ToString();
        var tenants = mongo.Database.GetCollection<Tenant>("Tenants");
        var saved = await tenants.Find(t => t.Id == tenant).SingleAsync();
        await tenants.ReplaceOneAsync(
            t => t.Id == tenant,
            saved with
            {
                Members = saved.Members.Add(
                    TenantFactory.CreateMember(tenant, member, "member@example.test", TenantRole.Member))
            });
        using var client = host.GetClient();
        client.DefaultRequestHeaders.Add(FeatureTestAuthentication.UserHeader, member);
        client.DefaultRequestHeaders.Add(TenantHeaders.TenantId, tenant);
        var archive = Route + "/archives/" + ObjectId.GenerateNewId();
        var body = new { name = "restored", idempotencyKey = "key", backupId = "backup", confirmed = true };
        using var restore = await client.PostAsJsonAsync(archive + "/restores", body);
        using var forget = await client.DeleteAsync(archive + "?confirmed=true");
        Assert.Equal(HttpStatusCode.Forbidden, restore.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forget.StatusCode);
        client.DefaultRequestHeaders.Add("X-Test-Platform-Admin", "true");
        using var platform = await client.PostAsJsonAsync(archive + "/restores", body);
        Assert.Equal(HttpStatusCode.NotFound, platform.StatusCode);
    }

    [Fact]
    public async Task Outbox_exception_rolls_back_destination_and_credential_grant()
    {
        await using var host = await setup.StartAsync(failDestination: true);
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var input = await BackupTestSetup.StorageAsync(client);
        await using var scope = host.Services.CreateAsyncScope();
        var save = scope.ServiceProvider.GetRequiredService<BackupDestinationWrites>();
        var id = ObjectId.GenerateNewId().ToString();
        await Assert.ThrowsAsync<InvalidOperationException>(() => save.CreateAsync(
            new(TestPlatformDefaults.TenantId, id, input),
            default));
        Assert.Equal(
            0,
            await mongo.Database
                .GetCollection<BackupDestination>("BackupDestinations")
                .CountDocumentsAsync(FilterDefinition<BackupDestination>.Empty));
        var use = scope.ServiceProvider.GetRequiredService<ReadConsumerSecretValues>();
        var grant = await use(
            new(
                TestPlatformDefaults.TenantId,
                input.ResourceGroupId,
                input.IsolationScopeId,
                BackupResourceTypes.Destination,
                id,
                input.CredentialSecretId,
                [input.AccessKeyIdKey, input.SecretAccessKeyKey]),
            default);
        Assert.IsType<ConsumerSecretAccessDenied>(grant.Value);
    }

    [Fact]
    public async Task Retained_placement_inherits_authorized_source_and_catalog_survives_source_removal()
    {
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, TestPlatformDefaults.TenantId);
        var input = await BackupTestSetup.StorageAsync(client);
        using var created = await client.PostAsJsonAsync(Route + "/destinations", input);
        created.EnsureSuccessStatusCode();
        var destination = (await created.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var sourceType = ResourceTypeId.Create(provider.ResourceType);
        var sourceId = ObjectId.GenerateNewId().ToString();
        var sourceDeployment = ObjectId.GenerateNewId().ToString();
        await using var scope = host.Services.CreateAsyncScope();
        await setup.SeedSourceAsync(scope.ServiceProvider, input, sourceType, sourceId, sourceDeployment);

        var policy = new BackupPolicyInput("daily", sourceType.Value, sourceId, destination.Id, "0 2 * * *", false, 30);
        using var accepted = await client.PostAsJsonAsync(Route + "/policies", policy);
        Assert.True(accepted.StatusCode == HttpStatusCode.Created, await accepted.Content.ReadAsStringAsync());
        var saved = (await accepted.Content.ReadFromJsonAsync<BackupPolicyDto>())!;
        var archive = await mongo.Database
            .GetCollection<BackupArchive>("BackupArchives")
            .Find(a => a.Id == saved.ArchiveId)
            .SingleAsync();
        var deployment = mongo.Database.GetCollection<ResourceDeployment>("ResourceDeployments");
        var retained = await deployment.Find(p => p.Id == archive.Placement.CredentialDeploymentId).SingleAsync();
        Assert.Equal(sourceType.Value, policy.ResourceType);
        Assert.Equal("pole/backup-destination", retained.ResourceType);
        Assert.Equal(destination.Id, retained.ResourceId);
        Assert.Equal(archive.Placement.RuntimeCellId, retained.RuntimeCellId);
        var denied = await scope.ServiceProvider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<bool>(async (tx, ct) =>
            {
                var result = await scope.ServiceProvider.GetRequiredService<RetainProjectionPlacement>()(
                    new(
                        TestPlatformDefaults.TenantId,
                        sourceType,
                        sourceId,
                        ObjectId.GenerateNewId().ToString(),
                        sourceType,
                        sourceId,
                        sourceDeployment),
                    tx.Session,
                    ct);
                return tx.Abort(result is CreateResourceDeploymentError);
            });
        Assert.True(denied);

        var dispatch = scope.ServiceProvider.GetRequiredService<ApplyBackupPolicy>();
        provider.Service = new("17", 10, 1, 2);
        Assert.True(await dispatch.ExecuteAsync(TestPlatformDefaults.TenantId, saved.Id, default));
        var live = await client.GetFromJsonAsync<BackupPolicyDto>(Route + "/policies/" + saved.Id);
        Assert.Equal(saved.Revision, live!.AppliedRevision);
        Assert.Equal("Healthy", live.WalArchivingStatus);
        var next = Assert.IsType<DateTime>(live.NextScheduledAt);
        Assert.Equal(2, next.Hour);
        Assert.Equal(0, next.Minute);
        Assert.True(next > DateTime.UtcNow);
        var archiveRoute = Route + "/archives/" + saved.ArchiveId;
        using var activeForget = await client.DeleteAsync(archiveRoute + "?confirmed=true");
        Assert.Equal(HttpStatusCode.Conflict, activeForget.StatusCode);
        Assert.True(
            await scope.ServiceProvider
                .GetRequiredService<ObserveBackupArchiveHistory>()
                .ExecuteAsync(TestPlatformDefaults.TenantId, saved.ArchiveId, default));
        var schedule = (await client.GetFromJsonAsync<BackupOperationPage>(Route + "/operations"))!.Items;
        Assert.All(schedule!, row => Assert.Equal("ScheduledBackup", row.Kind));
        var unfinished = Assert.Single(schedule!, row => row.State == "Running");
        var finished = Assert.Single(schedule!, row => row.State == "Succeeded");

        await deployment.DeleteOneAsync(d => d.Id == sourceDeployment);
        await scope.ServiceProvider
            .GetRequiredService<MongoRunTransaction>()
            .InvokeOutcomeAsync<bool>(async (tx, ct) =>
            {
                var result = await scope.ServiceProvider
                    .GetRequiredService<IResourceRegistrar>()
                    .UnregisterAsync(
                        new(TestPlatformDefaults.TenantId, input.ResourceGroupId, sourceType, sourceId),
                        tx.Session,
                        ct);
                Assert.IsType<ResourceUnregistered>(result.Value);
                return tx.Commit(true);
            });
        await scope.ServiceProvider
            .GetRequiredService<BackupSourceLifecycle>()
            .RetireAsync(
                new ResourceUnregisteredEvent(input.ResourceGroupId, sourceType.Value, sourceId)
                    { EventCore = EventCore.Create(TestPlatformDefaults.TenantId) },
                default);
        Assert.True(await dispatch.ExecuteAsync(TestPlatformDefaults.TenantId, saved.Id, default));
        var storedArchive = Assert.Single((await client.GetFromJsonAsync<BackupArchiveDto[]>(Route + "/archives"))!);
        Assert.False(storedArchive.Active);
        var historyRows = mongo.Database.GetCollection<BackupArchive>("BackupArchives");
        var historyArchive = await historyRows.Find(a => a.Id == saved.ArchiveId).SingleAsync();
        await historyRows.ReplaceOneAsync(
            a => a.Id == historyArchive.Id,
            historyArchive with
            {
                History = historyArchive.History.Retry(DateTime.UtcNow.AddSeconds(-1), TimeSpan.Zero, "source-removed")
            });
        Assert.True(
            await scope.ServiceProvider
                .GetRequiredService<ObserveBackupArchiveHistory>()
                .ExecuteAsync(TestPlatformDefaults.TenantId, saved.ArchiveId, default));
        var interrupted = await client.GetFromJsonAsync<BackupOperationDto>(Route + "/operations/" + unfinished.Id);
        Assert.Equal("Interrupted", interrupted!.State);
        Assert.Equal("source-removed-before-final-observation", interrupted.FailureCode);
        var complete = await client.GetFromJsonAsync<BackupOperationDto>(Route + "/operations/" + finished.Id);
        Assert.Equal("Succeeded", complete!.State);
        Assert.Null(complete.FailureCode);
        Assert.Equal(10, storedArchive.StorageGiB);
        var path = Route + $"/archives/{saved.ArchiveId}/backups";
        Assert.Empty((await client.GetFromJsonAsync<BackupCatalogEntry[]>(path))!);
        var backup = new BackupCatalogEntry("20261101T020000", DateTimeOffset.UtcNow.AddHours(-3));
        provider.Catalog = [backup];
        Assert.Equal(
            backup.BackupId,
            Assert.Single((await client.GetFromJsonAsync<BackupCatalogEntry[]>(path))!).BackupId);
        var restorePath = Route + $"/archives/{saved.ArchiveId}/restores";
        var body = new
        {
            idempotencyKey = "once", name = "restored", backupId = backup.BackupId, confirmed = true, storageGiB = 14
        };
        using var tooSmall = await client.PostAsJsonAsync(
            restorePath,
            new { body.idempotencyKey, body.name, body.backupId, body.confirmed, storageGiB = 4 });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooSmall.StatusCode);
        using var response = await client.PostAsJsonAsync(restorePath, body);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var first = (await response.Content.ReadFromJsonAsync<BackupOperationDto>())!;
        using var repeated = await client.PostAsJsonAsync(restorePath, body);
        Assert.Equal(first.Id, (await repeated.Content.ReadFromJsonAsync<BackupOperationDto>())!.Id);
        using var conflict = await client.PostAsJsonAsync(
            restorePath,
            new { body.idempotencyKey, name = "another", body.backupId, body.confirmed });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var unfinishedForget = await client.DeleteAsync(archiveRoute + "?confirmed=true");
        Assert.Equal(HttpStatusCode.Conflict, unfinishedForget.StatusCode);
        Assert.True(
            await scope.ServiceProvider
                .GetRequiredService<ExecuteBackupOperation>()
                .ExecuteAsync(TestPlatformDefaults.TenantId, first.Id, default));
        Assert.Equal(14, provider.RestoreStorageGiB);
        var done = await client.GetFromJsonAsync<BackupOperationDto>(Route + "/operations/" + first.Id);
        Assert.Equal("Succeeded", done!.State);
        provider.Catalog = [];
        using var removed = await client.PostAsJsonAsync(
            restorePath,
            new { idempotencyKey = "two", body.name, body.backupId, body.confirmed });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, removed.StatusCode);
        provider.CatalogResponse = new BackupCatalogUnavailable();
        using var outage = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, outage.StatusCode);
        using var forgotten = await client.DeleteAsync(archiveRoute + "?confirmed=true");
        Assert.True(forgotten.StatusCode == HttpStatusCode.NoContent, await forgotten.Content.ReadAsStringAsync());
        using var missing = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var retiring = await deployment.Find(p => p.Id == retained.Id).SingleAsync();
        Assert.Equal(ResourceDeploymentOperation.Delete, retiring.DesiredOperation);
        using var pending = await client.DeleteAsync(Route + "/destinations/" + destination.Id + "?expectedRevision=1");
        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        await deployment.UpdateOneAsync(
            p => p.Id == retained.Id,
            Builders<ResourceDeployment>.Update.Set(
                d => d.State,
                new DeletedResourceDeploymentState(retiring.DesiredGeneration, DateTime.UtcNow)));
        using var deleted = await client.DeleteAsync(Route + "/destinations/" + destination.Id + "?expectedRevision=1");
        Assert.True(deleted.StatusCode == HttpStatusCode.NoContent, await deleted.Content.ReadAsStringAsync());
        Assert.False(await deployment.Find(p => p.Id == retained.Id).AnyAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deleted_restore_target_cannot_be_recreated_after_delivery_retry(bool crashBeforeProgress)
    {
        const string tenant = TestPlatformDefaults.TenantId;
        await using var host = await setup.StartAsync();
        using var client = BackupTestSetup.Client(host, tenant);
        var input = await BackupTestSetup.StorageAsync(client);
        using var destinationResponse = await client.PostAsJsonAsync(Route + "/destinations", input);
        destinationResponse.EnsureSuccessStatusCode();
        var destination = (await destinationResponse.Content.ReadFromJsonAsync<BackupDestinationDto>())!;
        var provider = host.Services.GetRequiredService<TestBackupProvider>();
        var type = ResourceTypeId.Create(provider.ResourceType);
        var sourceId = ObjectId.GenerateNewId().ToString();
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        await setup.SeedSourceAsync(services, input, type, sourceId, ObjectId.GenerateNewId().ToString());
        using var policyResponse = await client.PostAsJsonAsync(
            Route + "/policies",
            new BackupPolicyInput("daily", type.Value, sourceId, destination.Id, "0 2 * * *", false, 30));
        policyResponse.EnsureSuccessStatusCode();
        var policy = (await policyResponse.Content.ReadFromJsonAsync<BackupPolicyDto>())!;
        provider.Catalog = [new("base", DateTimeOffset.UtcNow.AddHours(-2))];
        var body = new { backupId = "base", idempotencyKey = "restore", confirmed = true, name = "new-database" };
        using var response = await client.PostAsJsonAsync(Route + "/archives/" + policy.ArchiveId + "/restores", body);
        response.EnsureSuccessStatusCode();
        var command = (await response.Content.ReadFromJsonAsync<BackupOperationDto>())!;
        var transactions = services.GetRequiredService<MongoRunTransaction>();
        var receipts = services.GetRequiredService<RecordRestoreTargetCreation>();
        var registrar = services.GetRequiredService<IResourceRegistrar>();
        var identity = new RecordRestoreTargetCreationRequest(
            tenant,
            command.Id,
            policy.ArchiveId,
            command.NewResourceId!);
        await transactions.InvokeOutcomeAsync<bool>(async (tx, ct) =>
        {
            Assert.Equal(
                RestoreTargetCreationDecision.OperationUnavailable,
                await receipts(identity with { TenantId = ObjectId.GenerateNewId().ToString() }, tx.Session, ct));
            Assert.Equal(
                RestoreTargetCreationDecision.OperationUnavailable,
                await receipts(identity with { NewResourceId = sourceId }, tx.Session, ct));
            Assert.Equal(RestoreTargetCreationDecision.Recorded, await receipts(identity, tx.Session, ct));
            return tx.Abort(false);
        });
        var nativeCalls = 0;
        provider.CreateTarget = async (request, ct) =>
        {
            nativeCalls++;
            await transactions.InvokeOutcomeAsync<bool>(
                async (tx, token) =>
                {
                    Assert.Equal(RestoreTargetCreationDecision.Recorded, await receipts(identity, tx.Session, token));
                    var result = await registrar.RegisterAsync(
                        new(tenant, input.ResourceGroupId, new(type, request.Resource.Id, request.Resource.Name)),
                        tx.Session,
                        token);
                    Assert.IsType<RegisteredResource>(result.Value);
                    return tx.Commit(true);
                },
                ct);
            if (crashBeforeProgress)
                throw new InvalidOperationException("Simulated interruption after target creation committed.");
            return new BackupRestoreInProgress();
        };
        var dispatch = services.GetRequiredService<ExecuteBackupOperation>();
        if (crashBeforeProgress)
            await Assert.ThrowsAsync<InvalidOperationException>(() => dispatch.ExecuteAsync(
                tenant,
                command.Id,
                default));
        else
            Assert.True(await dispatch.ExecuteAsync(tenant, command.Id, default));
        var collection = mongo.Database.GetCollection<BackupOperation>("BackupOperations");
        var beforeRemoval = await collection.Find(o => o.Id == command.Id).SingleAsync();
        Assert.Equal(BackupOperationState.Running, beforeRemoval.State);
        Assert.IsType<ProvisioningBackupRestore>(Assert.IsType<RestoreBackupWork>(beforeRemoval.Work).Execution);
        await transactions.InvokeOutcomeAsync<bool>(async (tx, ct) =>
        {
            var result = await registrar.UnregisterAsync(
                new(tenant, input.ResourceGroupId, type, command.NewResourceId!),
                tx.Session,
                ct);
            Assert.IsType<ResourceUnregistered>(result.Value);
            return tx.Commit(true);
        });
        await transactions.InvokeOutcomeAsync<bool>(async (tx, ct) =>
        {
            Assert.Equal(RestoreTargetCreationDecision.AlreadyRecorded, await receipts(identity, tx.Session, ct));
            return tx.Abort(false);
        });
        // Simulate the first delivery's lease/poll interval elapsing after the user deleted the cluster.
        var expired = DateTime.UtcNow.AddMinutes(-5);
        await collection.UpdateOneAsync(
            o => o.Id == command.Id,
            Builders<BackupOperation>.Update.Set(o => o.Work, beforeRemoval.Retry(expired, TimeSpan.Zero, null).Work));
        provider.CatalogResponse = new BackupCatalogUnavailable();
        Assert.True(await dispatch.ExecuteAsync(tenant, command.Id, default));
        var retried = await client.GetFromJsonAsync<BackupOperationDto>(Route + "/operations/" + command.Id);
        Assert.Equal("Failed", retried!.State);
        Assert.Equal("restore-resource-deleted", retried.FailureCode);
        Assert.Equal(1, nativeCalls);
    }
}
