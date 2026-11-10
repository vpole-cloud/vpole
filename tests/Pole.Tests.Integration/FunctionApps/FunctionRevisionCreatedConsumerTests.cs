using System.Collections.Immutable;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.Events.Abstractions;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Application.FunctionApps;
using Pole.Features.Functions.Application.Lifecycle;
using Pole.Features.Functions.Application.Runtime.Provisioning;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Events;
using Pole.Features.Functions.Runtime.Consumers;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Provisioning;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Tests.Integration.DeploymentTargets;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

[Collection(IntegrationTestCollection.Tenant2)]
public sealed class FunctionRevisionCreatedConsumerTests(
    MongoReplicaSetFixture fixture
) : MediatorIntegrationTestBase(fixture)
{
    [Theory]
    [InlineData(NetworkPolicyEnforcement.Unknown, false)]
    [InlineData(NetworkPolicyEnforcement.Unsupported, false)]
    [InlineData(NetworkPolicyEnforcement.Supported, true)]
    public async Task Applied_placement_does_not_bypass_revoked_or_rotated_target(
        NetworkPolicyEnforcement enforcement,
        bool rotateCredentials)
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(
            new CreateFunctionAppRequest(
                new CreateFunctionAppRequestBody(
                    "revoked-functions",
                    $"revoked-functions-{Data.TenantId}",
                    group.Id,
                    RevisionRequest(),
                    []),
                Data.TenantId))).RequireSuccess();
        var target = DeploymentTargetNetworkPolicyTests.Attest(
            DeploymentTargetNetworkPolicyTests.Target() with { Id = created.Placement.DeploymentTargetId },
            enforcement,
            false);
        if (rotateCredentials)
            target = target.ReplaceConnectionCredential(
                target.ClusterIdentity,
                DeploymentTargetConnectionFactory.CreateInCluster(2),
                target.UpdatedAtUtc.AddMinutes(1));
        Assert.Equal(NetworkPolicyReadiness.EnforcementRequired, target.ToNetworkPolicyDto().Readiness);
        var provider = new NoCallsKubernetesProvider();
        var manager = new FunctionRevisionKubernetesManager(
            provider,
            (id, ct) => Task.FromResult(new LookupDeploymentTargetOutcome(target.ToLookupDto())),
            (request, ct) =>
                Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(new RuntimeCellIngressBaselineReady()),
            [],
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>());
        var loadPlacement = Services.GetRequiredService<LoadExactResourceDeploymentRuntimePlacement>();
        var resolver = new FunctionResourcePlacementResolver(async (request, ct) =>
        {
            var placement = await loadPlacement(
                new LoadExactResourceDeploymentRuntimePlacementRequest(
                    TenantId: request.TenantId,
                    ResourceType: request.ResourceType,
                    ResourceId: request.ResourceId,
                    DeploymentId: request.DeploymentId),
                cancellationToken: ct);
            Assert.NotNull(placement);
            return placement with { Status = ResourceDeploymentStatus.Applied };
        });
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(DbContext.FunctionApps, DbContext.FunctionRevisions)
                .InvokeAsync,
            Services.GetRequiredService<LoadFunctionAppById>(),
            Services.GetRequiredService<LoadExactResourceDeploymentRuntimePlacement>(),
            resolver,
            manager,
            Services.GetRequiredService<FailFunctionRevision>(),
            [],
            NullLogger<FunctionRevisionCreatedConsumer>.Instance);
        await consumer.HandleAsync(
            new FunctionRevisionCreatedEvent(
                created.FunctionAppId,
                created.RevisionId,
                created.RevisionNumber,
                Placement(created),
                null!)
            {
                EventCore = EventCore.Create(Data.TenantId),
            });
        Assert.Equal(0, provider.Calls);
        var revision = await DbContext.FunctionRevisions.Find(item => item.Id == created.RevisionId).SingleAsync();
        Assert.Equal("kubernetes-apply", Assert.IsType<FailedFunctionRevisionStatus>(revision.Status).Failure.Code);
    }

    [Fact]
    public async Task Failed_kubernetes_apply_marks_revision_failed_and_redelivery_is_a_noop()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(
            new CreateFunctionAppRequest(
                new CreateFunctionAppRequestBody(
                    "consumer-functions",
                    $"consumer-functions-{Data.TenantId}",
                    group.Id,
                    RevisionRequest(),
                    []),
                Data.TenantId))).RequireSuccess();
        var manager = new RecordingKubernetesManager(
            new FunctionRevisionKubernetesOperationError(
                new FunctionRevisionKubernetesOperationRejected("Kubernetes rejected the manifest.")));
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(DbContext.FunctionApps, DbContext.FunctionRevisions)
                .InvokeAsync,
            Services.GetRequiredService<LoadFunctionAppById>(),
            Services.GetRequiredService<LoadExactResourceDeploymentRuntimePlacement>(),
            new FixedPlacementResolver(),
            manager,
            Services.GetRequiredService<FailFunctionRevision>(),
            [],
            NullLogger<FunctionRevisionCreatedConsumer>.Instance);
        var message = new FunctionRevisionCreatedEvent(
            created.FunctionAppId,
            created.RevisionId,
            created.RevisionNumber,
            Placement(created),
            null!)
        {
            EventCore = EventCore.Create(Data.TenantId),
        };

        await consumer.HandleAsync(message);
        await consumer.HandleAsync(message);

        Assert.Equal(1, manager.ApplyCalls);
        var revision = await DbContext.FunctionRevisions
            .Find(candidate => candidate.Id == created.RevisionId)
            .SingleAsync();
        var failed = Assert.IsType<FailedFunctionRevisionStatus>(revision.Status);
        Assert.Equal("kubernetes-apply", failed.Failure.Code);
        var app = await DbContext.FunctionApps.Find(candidate => candidate.Id == created.FunctionAppId).SingleAsync();
        Assert.Null(app.PendingRevisionId);
        Assert.Equal(FunctionAppStatus.Failed, app.Status);
    }

    [Fact]
    public async Task Missing_runtime_authentication_fails_closed_after_manifest_apply()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(
            new CreateFunctionAppRequest(
                new CreateFunctionAppRequestBody(
                    "unconfigured-runtime-functions",
                    $"unconfigured-runtime-functions-{Data.TenantId}",
                    group.Id,
                    RevisionRequest(),
                    []),
                Data.TenantId))).RequireSuccess();
        var manager = new RecordingKubernetesManager(new FunctionRevisionKubernetesMutationCompleted());
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(DbContext.FunctionApps, DbContext.FunctionRevisions)
                .InvokeAsync,
            Services.GetRequiredService<LoadFunctionAppById>(),
            Services.GetRequiredService<LoadExactResourceDeploymentRuntimePlacement>(),
            new FixedPlacementResolver(),
            manager,
            Services.GetRequiredService<FailFunctionRevision>(),
            [],
            NullLogger<FunctionRevisionCreatedConsumer>.Instance);
        var message = new FunctionRevisionCreatedEvent(
            created.FunctionAppId,
            created.RevisionId,
            created.RevisionNumber,
            Placement(created),
            null!)
        {
            EventCore = EventCore.Create(Data.TenantId),
        };

        await consumer.HandleAsync(message);

        var revision = await DbContext.FunctionRevisions
            .Find(candidate => candidate.Id == created.RevisionId)
            .SingleAsync();
        var failed = Assert.IsType<FailedFunctionRevisionStatus>(revision.Status);
        Assert.Equal("runtime-auth-unconfigured", failed.Failure.Code);
        Assert.Equal(1, manager.ApplyCalls);
    }

    [Fact]
    public async Task Ready_revision_redelivery_resumes_provisioner_without_reapplying_manifest()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(
            new CreateFunctionAppRequest(
                new CreateFunctionAppRequestBody(
                    "ready-redelivery-functions",
                    $"ready-redelivery-functions-{Data.TenantId}",
                    group.Id,
                    RevisionRequest(),
                    []),
                Data.TenantId))).RequireSuccess();
        var markReady = Services.GetRequiredService<MarkFunctionRevisionReady>();
        var fail = Services.GetRequiredService<FailFunctionRevision>();
        (await markReady(Data.TenantId, created.FunctionAppId, created.RevisionId)).RequireLifecycleSuccess();
        var manager = new RecordingKubernetesManager(new FunctionRevisionKubernetesMutationCompleted());
        var provisioner = new RecordingProvisioner();
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(DbContext.FunctionApps, DbContext.FunctionRevisions)
                .InvokeAsync,
            Services.GetRequiredService<LoadFunctionAppById>(),
            Services.GetRequiredService<LoadExactResourceDeploymentRuntimePlacement>(),
            new FixedPlacementResolver(),
            manager,
            fail,
            [provisioner],
            NullLogger<FunctionRevisionCreatedConsumer>.Instance);
        var message = new FunctionRevisionCreatedEvent(
            created.FunctionAppId,
            created.RevisionId,
            created.RevisionNumber,
            Placement(created),
            null!)
        {
            EventCore = EventCore.Create(Data.TenantId),
        };

        await consumer.HandleAsync(message);

        Assert.Equal(0, manager.ApplyCalls);
        Assert.IsType<ReadyFunctionRevisionStatus>(Assert.IsType<FunctionRevision>(provisioner.Revision).Status);
    }

    [Fact]
    public async Task Ready_revision_without_runtime_authentication_is_stopped_before_failure()
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(
            new CreateFunctionAppRequest(
                new CreateFunctionAppRequestBody(
                    "ready-unconfigured-functions",
                    $"ready-unconfigured-functions-{Data.TenantId}",
                    group.Id,
                    RevisionRequest(),
                    []),
                Data.TenantId))).RequireSuccess();
        var markReady = Services.GetRequiredService<MarkFunctionRevisionReady>();
        var fail = Services.GetRequiredService<FailFunctionRevision>();
        (await markReady(Data.TenantId, created.FunctionAppId, created.RevisionId)).RequireLifecycleSuccess();
        var manager = new RecordingKubernetesManager(new FunctionRevisionKubernetesMutationCompleted());
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(DbContext.FunctionApps, DbContext.FunctionRevisions)
                .InvokeAsync,
            Services.GetRequiredService<LoadFunctionAppById>(),
            Services.GetRequiredService<LoadExactResourceDeploymentRuntimePlacement>(),
            new FixedPlacementResolver(),
            manager,
            fail,
            [],
            NullLogger<FunctionRevisionCreatedConsumer>.Instance);
        var message = new FunctionRevisionCreatedEvent(
            created.FunctionAppId,
            created.RevisionId,
            created.RevisionNumber,
            Placement(created),
            null!)
        {
            EventCore = EventCore.Create(Data.TenantId),
        };

        await consumer.HandleAsync(message);

        Assert.Equal([0], manager.BootstrapScaleRequests);
        var revision = await DbContext.FunctionRevisions
            .Find(candidate => candidate.Id == created.RevisionId)
            .SingleAsync();
        Assert.IsType<FailedFunctionRevisionStatus>(revision.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_dependency_restoration_replays_same_pending_revision(bool secretProjectionPending)
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(
            new CreateFunctionAppRequest(
                new CreateFunctionAppRequestBody(
                    "restored-gateway-functions",
                    $"restored-gateway-functions-{Data.TenantId}",
                    group.Id,
                    RevisionRequest(),
                    []),
                Data.TenantId))).RequireSuccess();
        FunctionRevisionKubernetesOperationError dependencyFailure = secretProjectionPending
            ? new FunctionRevisionSecretsUnavailable("Secret projection is not ready.")
            : new FunctionRevisionGatewaySourceUnavailable("The Function gateway source is not configured.");
        var manager = new RecordingKubernetesManager(dependencyFailure);
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(DbContext.FunctionApps, DbContext.FunctionRevisions)
                .InvokeAsync,
            Services.GetRequiredService<LoadFunctionAppById>(),
            AppliedPlacement(created),
            new FixedPlacementResolver(),
            manager,
            Services.GetRequiredService<FailFunctionRevision>(),
            [
                new CompletingProvisioner(
                    Services.GetRequiredService<MarkFunctionRevisionReady>(),
                    Services.GetRequiredService<PromoteFunctionRevision>()),
            ],
            NullLogger<FunctionRevisionCreatedConsumer>.Instance);
        var createdEvent = new FunctionRevisionCreatedEvent(
            created.FunctionAppId,
            created.RevisionId,
            created.RevisionNumber,
            Placement(created),
            null!)
        {
            EventCore = EventCore.Create(Data.TenantId),
        };

        await consumer.HandleAsync(createdEvent);
        var pending = await DbContext.FunctionRevisions
            .Find(candidate => candidate.Id == created.RevisionId)
            .SingleAsync();
        Assert.IsType<ProvisioningFunctionRevisionStatus>(pending.Status);
        var waitingApp = await DbContext.FunctionApps.Find(item => item.Id == created.FunctionAppId).SingleAsync();
        Assert.Equal(created.RevisionId, waitingApp.PendingRevisionId);
        Assert.Null(waitingApp.ActiveRevisionId);
        manager.ApplyResult = new FunctionRevisionKubernetesMutationCompleted();

        await consumer.HandleAppliedAsync(AppliedEvent(created));

        Assert.Equal(2, manager.ApplyCalls);
        var revision = await DbContext.FunctionRevisions
            .Find(candidate => candidate.Id == created.RevisionId)
            .SingleAsync();
        Assert.IsType<ActiveFunctionRevisionStatus>(revision.Status);
        var app = await DbContext.FunctionApps.Find(candidate => candidate.Id == created.FunctionAppId).SingleAsync();
        Assert.Null(app.PendingRevisionId);
        Assert.Equal(created.RevisionId, app.ActiveRevisionId);
        Assert.Equal(2, app.NextRevisionNumber);
        Assert.Equal(
            1,
            await DbContext.FunctionRevisions.CountDocumentsAsync(item => item.FunctionAppId == created.FunctionAppId));
    }

    [Theory]
    [InlineData(FunctionAppDesiredState.Deleting)]
    [InlineData(FunctionAppDesiredState.Prepared)]
    public async Task Events_do_not_replay_stale_generation_or_inert_app(FunctionAppDesiredState inertState)
    {
        var group = await DbContext.InsertResourceGroupAsync(Data.SeedResourceGroup());
        var created = (await Mediator.Send(
            new CreateFunctionAppRequest(
                new CreateFunctionAppRequestBody(
                    "stale-gateway-functions",
                    $"stale-gateway-functions-{Data.TenantId}",
                    group.Id,
                    RevisionRequest(),
                    []),
                Data.TenantId))).RequireSuccess();
        var manager = new RecordingKubernetesManager(new FunctionRevisionKubernetesMutationCompleted());
        var loadApp = Services.GetRequiredService<LoadFunctionAppById>();
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(DbContext.FunctionApps, DbContext.FunctionRevisions)
                .InvokeAsync,
            loadApp,
            AppliedPlacement(created),
            new FixedPlacementResolver(),
            manager,
            Services.GetRequiredService<FailFunctionRevision>(),
            [new RecordingProvisioner()],
            NullLogger<FunctionRevisionCreatedConsumer>.Instance);

        await consumer.HandleAppliedAsync(AppliedEvent(created) with { Generation = 2 });
        await DbContext.FunctionApps.UpdateOneAsync(
            item => item.Id == created.FunctionAppId,
            Builders<FunctionApp>.Update.Set(item => item.DesiredState, inertState));
        await consumer.HandleAppliedAsync(AppliedEvent(created));
        await consumer.HandleAsync(
            new FunctionRevisionCreatedEvent(
                created.FunctionAppId,
                created.RevisionId,
                created.RevisionNumber,
                Placement(created),
                null!)
            {
                EventCore = EventCore.Create(Data.TenantId),
            });
        await consumer.HandleAppliedAsync(
            AppliedEvent(created) with
            {
                EventCore = EventCore.Create("other-tenant"),
            });

        Assert.Equal(0, manager.ApplyCalls);
        var revision = await DbContext.FunctionRevisions
            .Find(candidate => candidate.Id == created.RevisionId)
            .SingleAsync();
        Assert.IsType<ProvisioningFunctionRevisionStatus>(revision.Status);
    }

    private static FunctionRevisionRequest RevisionRequest() => new(
        new FunctionImageRequest("registry.example", "team/functions", "v1"),
        new FunctionConfiguration(
            ImmutableDictionary<string, string>.Empty,
            ImmutableDictionary<string, FunctionSecretReference>.Empty),
        new FunctionResources(new FunctionResourceValues(100, 128), new FunctionResourceValues(500, 512)),
        new FunctionScalingPolicy(0, 5, 4, 8, 60, 30),
        [
            new FunctionHandlerRequest(
                "webhook",
                4,
                new HttpFunctionTriggerRequest("orders", ["POST"], FunctionExposure.Internal)),
        ]);

    private static FunctionPlacementSnapshot Placement(CreateFunctionAppResponse created) => new(
        created.Placement.DeploymentId,
        created.Placement.RuntimeCellId,
        created.Placement.DeploymentTargetId);

    private LoadExactResourceDeploymentRuntimePlacement AppliedPlacement(CreateFunctionAppResponse created) =>
        (request, ct) => Task.FromResult<ResourceDeploymentRuntimePlacementDto?>(
            request.TenantId == Data.TenantId && request.ResourceType == FunctionResourceDeployment.ResourceType &&
            request.ResourceId == created.FunctionAppId && request.DeploymentId == created.Placement.DeploymentId
                ? new ResourceDeploymentRuntimePlacementDto(
                    request.DeploymentId,
                    request.ResourceId,
                    created.Placement.RuntimeCellId,
                    created.Placement.DeploymentTargetId,
                    "functions",
                    1,
                    1,
                    null!,
                    ResourceDeploymentStatus.Applied,
                    ResourceDeploymentDesiredOperation.Apply,
                    "test-scope")
                : null);

    private ResourceDeploymentAppliedEvent AppliedEvent(CreateFunctionAppResponse created) => new(
        created.Placement.DeploymentId,
        FunctionResourceDeployment.ResourceType.Value,
        created.FunctionAppId,
        created.Placement.RuntimeCellId,
        1)
    {
        EventCore = EventCore.Create(Data.TenantId),
    };

    private sealed class NoCallsKubernetesProvider : IKubernetesClientProvider
    {
        public int Calls { get; private set; }

        public Task<IKubernetes> GetAsync(string deploymentTargetId, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("A revoked target must not reach Kubernetes.");
        }

        public Task<IKubernetes> GetForCleanupAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default) => GetAsync(deploymentTargetId, cancellationToken);

        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Default target must not be used.");
    }

    private sealed class FixedPlacementResolver : IFunctionResourcePlacementResolver
    {
        public Task<FunctionResourcePlacementOutcome> ResolveAsync(
            FunctionApp app,
            bool requireReady = true,
            CancellationToken cancellationToken = default) => Task.FromResult<FunctionResourcePlacementOutcome>(
            new FunctionResourcePlacement(
                app.Placement.ResourceDeploymentId,
                app.Placement.RuntimeCellId,
                app.Placement.DeploymentTargetId,
                "functions",
                "507f1f77bcf86cd799439044"));
    }

    private sealed class RecordingKubernetesManager(
        FunctionRevisionKubernetesMutationOutcome applyResult
    ) : IFunctionRevisionKubernetesManager
    {
        public int ApplyCalls { get; private set; }
        public FunctionRevisionKubernetesMutationOutcome ApplyResult { get; set; } = applyResult;
        public List<int> BootstrapScaleRequests { get; } = [];

        public Task<FunctionRevisionKubernetesMutationOutcome> ApplyAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            return Task.FromResult(ApplyResult);
        }

        public Task<FunctionRevisionKubernetesMutationOutcome> DeleteAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FunctionRevisionKubernetesMutationOutcome>(
                new FunctionRevisionKubernetesMutationCompleted());

        public Task<FunctionRevisionKubernetesMutationOutcome> ScaleProvisioningAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            CancellationToken cancellationToken = default)
        {
            BootstrapScaleRequests.Add(replicas);
            return Task.FromResult<FunctionRevisionKubernetesMutationOutcome>(
                new FunctionRevisionKubernetesMutationCompleted());
        }

        public Task<FunctionRevisionKubernetesMutationOutcome> ScaleAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            long coordinationGeneration,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<FunctionReplicaStateOutcome> ReadReplicaStateAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FunctionReplicaStateOutcome>(new FunctionReplicaState(0, 0, 0));
    }

    private sealed class RecordingProvisioner : IFunctionRevisionProvisioner
    {
        public FunctionRevision? Revision { get; private set; }

        public Task<FunctionRevisionProvisioningOutcome> ProvisionAsync(
            FunctionRevisionInKubernetes subject,
            CancellationToken cancellationToken)
        {
            Revision = subject.Revision;
            return Task.FromResult<FunctionRevisionProvisioningOutcome>(new FunctionRevisionProvisioningCompleted());
        }
    }

    private sealed class CompletingProvisioner(
        MarkFunctionRevisionReady markReady,
        PromoteFunctionRevision promote
    ) : IFunctionRevisionProvisioner
    {
        public async Task<FunctionRevisionProvisioningOutcome> ProvisionAsync(
            FunctionRevisionInKubernetes subject,
            CancellationToken cancellationToken)
        {
            (await markReady(subject.App.TenantId, subject.App.Id, subject.Revision.Id, cancellationToken))
                .RequireLifecycleSuccess();
            (await promote(subject.App.TenantId, subject.App.Id, subject.Revision.Id, cancellationToken))
                .RequireLifecycleSuccess();
            return new FunctionRevisionProvisioningCompleted();
        }
    }
}
