using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using Pole.Database;
using Pole.Features.DeploymentTargets.Application.Admission;
using Pole.Features.DeploymentTargets.Application.References;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.PrivateAccess.Application;
using Pole.Features.PrivateAccess.Contracts;
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
                    var result = await apply(
                        new ApplyDeploymentTargetReferenceChangesRequest(
                            specialOwner,
                            [paused.Id],
                            []),
                        transaction.Session,
                        ct);
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
                        new ApplyDeploymentTargetReferenceChangesRequest(
                            new DeploymentTargetReferenceOwner(
                            "ordinary-workload", ObjectId.GenerateNewId().ToString()),
                            [paused.Id],
                            []),
                        transaction.Session,
                        ct);
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
                        new ApplyDeploymentTargetReferenceChangesRequest(
                            new DeploymentTargetReferenceOwner(
                            DeploymentTargetReferenceOwnerTypes.PrivateAccessSession,
                            ObjectId.GenerateNewId().ToString()),
                            [target.Id],
                            []),
                        transaction.Session,
                        ct);
                    return admission is DeploymentTargetReferencesChanged
                        ? transaction.Commit(admission)
                        : transaction.Abort(admission);
                });
        Assert.IsType<DeploymentTargetReferenceChangeRejected>(result.Value);
    }

    [Fact]
    public async Task GatewayRegistration_HoldsTargetReferenceUntilCleanupCompletes()
    {
        var target = CreateTarget();
        await fixture.DbContext.DeploymentTargets.InsertOneAsync(target);
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(fixture.Client);
        services.AddSingleton(fixture.Database);
        services.AddSingleton<MongoRunTransaction>();
        services.AddPrivateAccessFeature();
        services.AddSingleton<ApplyDeploymentTargetReferenceChanges>(
            new MongoApplyDeploymentTargetReferenceChanges(
                fixture.DbContext.DeploymentTargets,
                fixture.DbContext.DeploymentTargetReferences,
                TimeProvider.System).InvokeAsync);
        using var provider = services.BuildServiceProvider();
        var tenantId = ObjectId.GenerateNewId().ToString();
        var identity = new PrivateAccessGatewayIdentity(tenantId, target.Id,
            PrivateAccessGatewayIds.StableId(tenantId, target.Id), 1, 1);

        var saved = await provider.GetRequiredService<SavePrivateAccessGatewayRegistration>()(
            new SavePrivateAccessGatewayRegistrationRequest(
                Identity: identity,
                CertificateSha256Pin: new string('a', 64),
                CertificateNotAfterUtc: DateTime.UtcNow.AddDays(30),
                Endpoints: []));

        Assert.True(saved);
        var reference = Assert.Single(await fixture.DbContext.DeploymentTargetReferences
            .Find(item => item.DeploymentTargetId == target.Id).ToListAsync());
        Assert.Equal(DeploymentTargetReferenceOwnerTypes.PrivateAccessGateway,
            reference.OwnerType);
        Assert.Equal(identity.GatewayId, reference.OwnerId);
        Assert.False(await provider.GetRequiredService<RemovePrivateAccessGatewayRegistration>()(
            identity with { Revision = 2 }));
        Assert.True(await provider.GetRequiredService<RemovePrivateAccessGatewayRegistration>()(
            identity));
        Assert.Empty(await fixture.DbContext.DeploymentTargetReferences
            .Find(item => item.DeploymentTargetId == target.Id).ToListAsync());
    }

    private static DeploymentTarget CreateTarget()
    {
        var id = ObjectId.GenerateNewId().ToString();
        var now = DateTime.UtcNow;
        const long connectionVersion = 1;
        return DeploymentTargetFactory.Create("target-" + id,
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
