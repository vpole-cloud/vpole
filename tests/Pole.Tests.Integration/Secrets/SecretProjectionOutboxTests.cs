using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Secrets;

public sealed class SecretProjectionOutboxTests(MongoReplicaSetFixture fixture) : MediatorIntegrationTestBase(fixture)
{
    private const string EventName = "pole.secrets/v1/secret-projection-changed";

    private IMongoCollection<SecretProjection> Projections =>
        Fixture.Database.GetCollection<SecretProjection>("SecretProjections");

    [Theory]
    [InlineData("Applied")]
    [InlineData("Failed")]
    [InlineData("Deleted")]
    public async Task AcceptedClaimAndCompletionCommitOnlyIdentifiersAndVersions(string state)
    {
        var desired = Desired();
        var claimed = Assert.IsType<SecretProjection>(await Claim(desired, "owner", DateTime.UtcNow));
        var completed = state switch
        {
            "Applied" => claimed.Applied(),
            "Deleted" => claimed.Deleted(),
            _ => claimed.Failed("private-failure-detail")
        };
        await Complete(completed);
        await Complete(claimed.Applied());
        await Complete(claimed with
        {
            TenantId = ObjectId.GenerateNewId().ToString()
        });

        var envelopes = await DbContext.OutboxMessages.Find(row => row.EventType == EventName).ToListAsync();
        Assert.Equal(2, envelopes.Count);
        var messages = envelopes.Select(row => JsonSerializer.Deserialize<SecretProjectionChangedEvent>(
            row.PayloadJson,
            EventExtensions.SerializerOptions)!).OrderBy(row => row.StorageRevision).ToArray();
        Assert.Equal([2L, 3L], messages.Select(message => message.StorageRevision).ToArray());
        Assert.All(messages,
            message =>
            {
                Assert.Equal(desired.TenantId, message.EventCore.TenantId);
                Assert.Equal(desired.SecretId, message.SecretId);
                Assert.Equal(desired.ResourceGroupId, message.ResourceGroupId);
                Assert.Equal(desired.DeploymentId, message.DeploymentId);
            });
        Assert.All(envelopes,
            row =>
            {
                Assert.Equal(desired.TenantId, row.TenantId);
                Assert.DoesNotContain("private-failure-detail", row.PayloadJson);
                Assert.DoesNotContain("private-namespace", row.PayloadJson);
                Assert.DoesNotContain("private-kubernetes-name", row.PayloadJson);
                Assert.DoesNotContain("owner", row.PayloadJson);
            });
    }

    [Fact]
    public async Task BusyLeaseExpiredOwnerAndOlderDesiredRevisionDoNotWriteHints()
    {
        var desired = Desired();
        var now = DateTime.UtcNow;
        var old = Assert.IsType<SecretProjection>(await Claim(desired, "old", now));
        Assert.Null(await Claim(desired, "busy", now.AddSeconds(1)));
        var current = Assert.IsType<SecretProjection>(
            await Claim(desired with
            {
                DesiredRevision = 2,
                DesiredVersion = 2
            }, "current", now.AddMinutes(3)));
        await Complete(current.Applied());
        await Complete(old.Failed("stale"));
        Assert.Null(await Claim(desired, "older", now.AddMinutes(4)));
        Assert.Equal(3, await DbContext.CountOutboxMessagesAsync(EventName));
        var stored = await Projections.Find(row => row.Id == desired.Id).SingleAsync();
        Assert.Equal(2, stored.AppliedVersion);
        Assert.IsType<ReadySecretProjection>(stored.State);
        Assert.IsType<UnclaimedSecretProjection>(stored.Lease);
    }

    [Fact]
    public async Task ConcurrentFirstDiscoveryCommitsOneClaimAndOneHint()
    {
        var desired = Desired();
        var now = DateTime.UtcNow;
        var results =
            await Task.WhenAll(Enumerable.Range(0, 8).Select(index => Claim(desired, $"worker-{index}", now)));
        Assert.Single(results, result => result is not null);
        Assert.Equal(1, await Projections.CountDocumentsAsync(row => row.Id == desired.Id));
        Assert.Equal(1, await DbContext.CountOutboxMessagesAsync(EventName));
    }

    [Theory]
    [InlineData("discovery")]
    [InlineData("claim")]
    [InlineData("completion")]
    public async Task OutboxFailureRollsBackProjectionMutationAndItsOutbox(string mutation)
    {
        var desired = Desired();
        SecretProjection? before = mutation switch
        {
            "claim" => desired.Applied(),
            "completion" => desired.Claim("owner", DateTime.UtcNow),
            _ => null
        };
        if (before is not null)
        {
            await Projections.InsertOneAsync(before);
            before = await Projections.Find(row => row.Id == desired.Id).SingleAsync();
        }

        await using var provider = FailingOutboxProvider();
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => mutation == "completion"
            ? scope.ServiceProvider.GetRequiredService<CompleteSecretProjection>()(before!.Applied(), default)
            : scope.ServiceProvider.GetRequiredService<ClaimSecretProjection>()(desired,
                "new-owner",
                DateTime.UtcNow,
                default));
        var after = await Projections.Find(row => row.Id == desired.Id).SingleOrDefaultAsync();
        Assert.Equal(before, after);
        Assert.Equal(0, await DbContext.CountOutboxMessagesAsync(EventName));
    }

    private Task<SecretProjection?> Claim(SecretProjection desired, string owner, DateTime now) =>
        Services.GetRequiredService<ClaimSecretProjection>()(desired, owner, now, default);

    private Task Complete(SecretProjection projection) =>
        Services.GetRequiredService<CompleteSecretProjection>()(projection, default);

    private ServiceProvider FailingOutboxProvider()
    {
        var services = new ServiceCollection().AddSingleton(Fixture.Database).AddSingleton(Fixture.Client)
            .AddSingleton<MongoRunTransaction>();
        services.AddSecretsFeature();
        services.AddSingleton<StoreOutboxWrites>(async (session, writes, ct) =>
        {
            await Fixture.StoreOutboxWrites(session, writes, ct);
            throw new InvalidOperationException("Injected outbox failure.");
        });
        return services.BuildServiceProvider();
    }

    private static SecretProjection Desired()
    {
        var secret = SecretFactory.Create(ObjectId.GenerateNewId().ToString(),
            ObjectId.GenerateNewId().ToString(),
            "group",
            "scope",
            "name",
            "purpose",
            ["key"],
            DateTime.UtcNow);
        return SecretProjectionFactory.Create(secret,
                SecretBinding.Create("binding", "pole/postgresql", "consumer", new AllSecretDeployments(), []),
                "deployment",
                "cell",
                "target",
                "private-namespace") with
        {
            KubernetesName = "private-kubernetes-name"
        };
    }
}
