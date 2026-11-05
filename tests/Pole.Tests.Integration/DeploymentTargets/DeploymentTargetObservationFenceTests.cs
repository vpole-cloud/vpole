using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application.Mongo;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.DeploymentTargets;

[Collection(IntegrationTestCollection.Name)]
public sealed class DeploymentTargetObservationFenceTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Observation_fence_preserves_admission_revision_references_and_metadata()
    {
        var target = Create() with
        {
            AcceptsWorkloads = false,
            ReferenceFence = 17
        };
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        using var session = await fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var result = await Fence().InvokeAsync(new(target.Id, target.Connection.Version), session);
        Assert.IsType<DeploymentTargetObservationFenced>(result.Value);
        var beforeCommit = await fixture.DbContext.DeploymentTargets.Find(x => x.Id == target.Id).SingleAsync();
        Assert.Equal(0, beforeCommit.ObservationFence);
        await session.CommitTransactionAsync();
        var current = await fixture.DbContext.DeploymentTargets.Find(x => x.Id == target.Id).SingleAsync();
        Assert.Equal((target with
        {
            ObservationFence = 1
        }).ToBsonDocument(), current.ToBsonDocument());
        // Observation must not spuriously reject a reference-aware mutation loaded earlier.
        Assert.True(await fixture.DbContext.DeploymentTargets
            .Find(DeploymentTargetFilters.AtRevisionAndReferenceFence(target)).AnyAsync());
    }

    [Theory]
    [InlineData("rotated")]
    [InlineData("disabled")]
    [InlineData("deleting")]
    [InlineData("missing")]
    public async Task Stale_or_unavailable_target_is_rejected_without_a_fence_write(string state)
    {
        var target = Create();
        var stored = state switch
        {
            "rotated" => target with { Connection = DeploymentTargetConnectionFactory.CreateInCluster(2) },
            "disabled" => target with { Enabled = false },
            "deleting" => target with { DeletionRequestedAtUtc = DateTime.UtcNow },
            _ => target
        };
        if (state != "missing")
            await fixture.DbContext.DeploymentTargets.InsertOneAsync(stored);
        using var session = await fixture.Client.StartSessionAsync();
        session.StartTransaction();
        var result = await Fence().InvokeAsync(new(target.Id, 1), session);
        Assert.IsType<DeploymentTargetObservationFenceRejected>(result.Value);
        await session.AbortTransactionAsync();
        var current = await fixture.DbContext.DeploymentTargets.Find(x => x.Id == target.Id).FirstOrDefaultAsync();
        if (state == "missing")
            Assert.Null(current);
        else
            Assert.Equal(stored.ToBsonDocument(), current!.ToBsonDocument());
    }

    [Fact]
    public async Task Aborting_the_owner_transaction_rolls_back_the_target_observation_fence()
    {
        var target = Create();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        using var session = await fixture.Client.StartSessionAsync();
        session.StartTransaction();
        Assert.IsType<DeploymentTargetObservationFenced>((await Fence().InvokeAsync(new(target.Id, 1), session)).Value);
        await session.AbortTransactionAsync();
        Assert.Equal(target.ToBsonDocument(),
            (await fixture.DbContext.DeploymentTargets.Find(x => x.Id == target.Id).SingleAsync()).ToBsonDocument());
    }

    [Fact]
    public async Task Fence_requires_an_active_transaction_and_positive_version()
    {
        using var session = await fixture.Client.StartSessionAsync();
        var request = new DeploymentTargetObservationFenceRequest(ObjectId.GenerateNewId().ToString(), 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Fence().InvokeAsync(request, session));
        session.StartTransaction();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Fence().InvokeAsync(request with
            {
                ExpectedConnectionVersion = 0
            }, session));
        await session.AbortTransactionAsync();
    }

    private MongoFenceDeploymentTargetObservation Fence() => new(fixture.DbContext.DeploymentTargets);

    private static DeploymentTarget Create()
    {
        var id = ObjectId.GenerateNewId().ToString();
        var now = DateTime.UtcNow;
        return DeploymentTargetFactory.Create("target-" + id, "Observation target",
            $"kubernetes:kube-system:{id}",
            DeploymentTargetConnectionFactory.CreateInCluster(1),
            DeploymentTargetHealth.Healthy(1, now),
            DeploymentTargetCapabilities.Observed(1,
                now,
                "v1.35.0",
                new DeploymentTargetNamespaceCapabilities(true, true, true, true, true),
                new DeploymentTargetNetworkPolicyCapabilities(true, true, true, true, true)),
            DeploymentTargetTestData.CreateWorkloadConfiguration(),
            true,
            true,
            new DeploymentTargetFactory.CreateOptions(id, now)).AttestForTests();
    }
}
