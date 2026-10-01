using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Persistence.Connectors;
using Pole.Features.DeploymentTargets.Application.Persistence.References;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.PrivateAccess;

[Collection(IntegrationTestCollection.Name)]
public sealed class GrantTargetReferenceTests(MongoReplicaSetFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PrivateAccessReference_AdmitsAttestedPausedTarget_WithoutRelaxingOrdinaryOwners()
    {
        var target = CreateTarget();
        var paused = target.SetWorkloadAdmission(false, target.UpdatedAtUtc.AddSeconds(1));
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(paused);
        var apply = new MongoApplyDeploymentTargetReferenceChanges(
            fixture.DbContext.DeploymentTargets,
            fixture.DbContext.DeploymentTargetReferences,
            TimeProvider.System).InvokeAsync;
        var specialOwner = new DeploymentTargetReferenceOwner(
            DeploymentTargetReferenceOwnerTypes.PrivateAccessSession,
            ObjectId.GenerateNewId().ToString());
        var added = await new MongoRunTransaction(fixture.Client)
            .InvokeOutcomeAsync<ApplyDeploymentTargetReferenceChangesOutcome>(
                async (transaction, ct) =>
                {
                    var result = await apply(specialOwner, [paused.Id], [],
                        transaction.Session, ct);
                    return result is DeploymentTargetReferencesChanged
                        ? transaction.Commit(result)
                        : transaction.Abort(result);
                });
        Assert.IsType<DeploymentTargetReferencesChanged>(added.Value);
        Assert.Equal(1, await fixture.DbContext.DeploymentTargetReferences
            .CountDocumentsAsync(item => item.OwnerType ==
                DeploymentTargetReferenceOwnerTypes.PrivateAccessSession));

        var ordinary = await new MongoRunTransaction(fixture.Client)
            .InvokeOutcomeAsync<ApplyDeploymentTargetReferenceChangesOutcome>(
                async (transaction, ct) =>
                {
                    var result = await apply(
                        new DeploymentTargetReferenceOwner(
                            "ordinary-workload", ObjectId.GenerateNewId().ToString()),
                        [paused.Id], [], transaction.Session, ct);
                    return result is DeploymentTargetReferencesChanged
                        ? transaction.Commit(result)
                        : transaction.Abort(result);
                });
        Assert.IsType<DeploymentTargetReferenceChangeRejected>(ordinary.Value);

        var fence = new MongoFenceDeploymentTargetPrivateAccess(
            fixture.DbContext.DeploymentTargets).InvokeAsync;
        var checkedTarget = await new MongoRunTransaction(fixture.Client)
            .InvokeOutcomeAsync<FenceDeploymentTargetPrivateAccessOutcome>(
                async (transaction, ct) =>
                {
                    var result = await fence(paused.Id, transaction.Session, ct);
                    return result is PrivateAccessDeploymentTargetReady
                        ? transaction.Commit(result)
                        : transaction.Abort(result);
                });
        Assert.IsType<PrivateAccessDeploymentTargetReady>(checkedTarget.Value);
    }

    [Fact]
    public async Task PrivateAccessReference_RejectsIsolationOverrideWithoutAttestedEnforcement()
    {
        var target = CreateTarget().AttestNetworkPolicy(
            NetworkPolicyEnforcement.Unsupported,
            allowIsolationNotEnforced: true,
            DateTime.UtcNow.AddSeconds(1));
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var apply = new MongoApplyDeploymentTargetReferenceChanges(
            fixture.DbContext.DeploymentTargets,
            fixture.DbContext.DeploymentTargetReferences,
            TimeProvider.System).InvokeAsync;
        var result = await new MongoRunTransaction(fixture.Client)
            .InvokeOutcomeAsync<ApplyDeploymentTargetReferenceChangesOutcome>(
                async (transaction, ct) =>
                {
                    var admission = await apply(
                        new DeploymentTargetReferenceOwner(
                            DeploymentTargetReferenceOwnerTypes.PrivateAccessSession,
                            ObjectId.GenerateNewId().ToString()),
                        [target.Id], [], transaction.Session, ct);
                    return admission is DeploymentTargetReferencesChanged
                        ? transaction.Commit(admission)
                        : transaction.Abort(admission);
                });
        Assert.IsType<DeploymentTargetReferenceChangeRejected>(result.Value);
    }

    private static DeploymentTarget CreateTarget()
    {
        var id = ObjectId.GenerateNewId().ToString();
        var now = DateTime.UtcNow;
        const long connectionVersion = 1;
        return DeploymentTargetFactory.Create(
            "Private access target",
            $"kubernetes:kube-system:{id}",
            DeploymentTargetConnectionFactory.CreateInCluster(connectionVersion),
            DeploymentTargetHealth.Healthy(connectionVersion, now),
            DeploymentTargetCapabilities.Observed(
                connectionVersion,
                now,
                "v1.35.0",
                new DeploymentTargetNamespaceCapabilities(true, true, true, true, true),
                new DeploymentTargetNetworkPolicyCapabilities(true, true, true, true, true)),
            DeploymentTargetTestData.CreateWorkloadConfiguration(),
            enabled: true,
            acceptsWorkloads: true,
            new DeploymentTargetFactory.CreateOptions(id, now)).AttestForTests();
    }
}
