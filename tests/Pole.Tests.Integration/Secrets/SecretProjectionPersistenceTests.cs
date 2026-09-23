using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.Secrets.Application.Persistence;
using Pole.Features.Secrets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.Secrets;

public sealed class SecretProjectionPersistenceTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Fact]
    public async Task Recovery_enumeration_is_bounded_and_retains_due_deleted_tombstones()
    {
        var tenant = ObjectId.GenerateNewId().ToString();
        var now = DateTime.UtcNow;
        SecretProjection Projection(string secretId, string deploymentId) => SecretProjectionFactory.Create(
            SecretFactory.Create(secretId, tenant, "group", "scope", "name", "purpose", ["password"], now),
            SecretBinding.Create("binding", "pole/postgresql", "resource", new AllSecretDeployments(), []),
            deploymentId, "cell", "target", "namespace");
        var rows = Enumerable.Range(0, 130).Select(_ => Projection(ObjectId.GenerateNewId().ToString(), "deployment")).ToList();
        rows.Add(Projection(rows[0].SecretId, "second-deployment"));
        var recent = Projection(ObjectId.GenerateNewId().ToString(), "recent").Deleted() with { LastAttemptAtUtc = now };
        var due = Projection(ObjectId.GenerateNewId().ToString(), "due").Deleted() with { LastAttemptAtUtc = now.AddHours(-1) };
        rows.Add(recent);
        rows.Add(due);
        await Fixture.Database.GetCollection<SecretProjection>("SecretProjections").InsertManyAsync(rows);
        var load = Services.GetRequiredService<LoadSecretProjectionCandidates>();
        var first = await load(null, now.AddMinutes(-30), default);
        Assert.Equal(128, first.Count);
        var second = await load(first[^1].SecretId, now.AddMinutes(-30), default);
        Assert.Equal(3, second.Count);
        var all = first.Concat(second).ToArray();
        Assert.Equal(131, all.Select(x => x.SecretId).Distinct().Count());
        Assert.Contains(all, x => x.SecretId == due.SecretId);
        Assert.DoesNotContain(all, x => x.SecretId == recent.SecretId);
        Assert.Empty(await load(second[^1].SecretId, now.AddMinutes(-30), default));
    }

    [Fact]
    public async Task Expired_owner_cannot_overwrite_new_owner_and_old_desired_version_cannot_regress()
    {
        var tenant = ObjectId.GenerateNewId().ToString();
        var secret = SecretFactory.Create(ObjectId.GenerateNewId().ToString(), tenant, "group", "scope", "name", "purpose", ["password"], DateTime.UtcNow);
        var binding = SecretBinding.Create("binding", "pole/postgresql", "resource", new AllSecretDeployments(), []);
        var desired = SecretProjectionFactory.Create(secret, binding, "deployment", "cell", "target", "namespace");
        var claim = Services.GetRequiredService<ClaimSecretProjection>();
        var complete = Services.GetRequiredService<CompleteSecretProjection>();
        var load = Services.GetRequiredService<LoadSecretProjections>();
        var now = DateTime.UtcNow;
        var first = Assert.IsType<SecretProjection>(await claim(desired, "first", now, default));
        Assert.Null(await claim(desired, "blocked", now, default));
        var rotated = desired with { DesiredVersion = 2, DesiredRevision = 2 };
        var second = Assert.IsType<SecretProjection>(await claim(rotated, "second", now.AddMinutes(3), default));
        await complete(second.Applied(), default);
        await complete(first.Failed("StaleFailure"), default);
        var stored = Assert.Single(await load(tenant, secret.Id, default));
        Assert.Equal(2, stored.AppliedVersion);
        Assert.IsType<ReadySecretProjection>(stored.State);
        Assert.IsType<UnclaimedSecretProjection>(stored.Lease);
        Assert.Null(await claim(desired, "stale", now.AddMinutes(4), default));
        Assert.Empty(await load(ObjectId.GenerateNewId().ToString(), secret.Id, default));
    }

    [Fact]
    public async Task Concurrent_claims_have_only_one_owner_and_preserve_last_applied_version()
    {
        var secret = SecretFactory.Create(ObjectId.GenerateNewId().ToString(), ObjectId.GenerateNewId().ToString(), "group", "scope", "name", "purpose", ["password"], DateTime.UtcNow);
        var desired = SecretProjectionFactory.Create(secret, SecretBinding.Create("binding", "pole/postgresql", "resource", new AllSecretDeployments(), []), "deployment", "cell", "target", "namespace");
        var claim = Services.GetRequiredService<ClaimSecretProjection>();
        var complete = Services.GetRequiredService<CompleteSecretProjection>();
        var now = DateTime.UtcNow;
        var initial = Assert.IsType<SecretProjection>(await claim(desired, "initial", now, default));
        await complete(initial.Applied(), default);
        var rotated = desired with { DesiredVersion = 2, DesiredRevision = 2 };
        var attempts = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => claim(rotated, $"worker-{i}", now, default)));
        var winner = Assert.Single(attempts, x => x is not null);
        Assert.Equal(1, winner!.AppliedVersion);
        Assert.Equal(2, winner.DesiredVersion);
    }
}
