using k8s;
using Pole.Features.DeploymentTargets.Application.Common;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.DeploymentTargets.Domain;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Tests.Integration.DeploymentTargets;
using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Application.Persistence.Lifecycle;
using Pole.Features.Functions.Application.Persistence.Runtime.Provisioning;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Events;
using Pole.Features.Functions.Runtime.Consumers;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Provisioning;
using Pole.ResultType;
using Pole.Tests.Integration.Infrastructure;
using Xunit;

namespace Pole.Tests.Integration.FunctionApps;

public sealed class FunctionRevisionCreatedConsumerTests(MongoReplicaSetFixture fixture)
    : MediatorIntegrationTestBase(fixture)
{
    [Theory]
    [InlineData(NetworkPolicyEnforcement.Unknown, false)]
    [InlineData(NetworkPolicyEnforcement.Unsupported, false)]
    [InlineData(NetworkPolicyEnforcement.Supported, true)]
    public async Task Applied_placement_does_not_bypass_revoked_or_rotated_target(
        NetworkPolicyEnforcement enforcement,
        bool rotateCredentials)
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = (await Mediator.Send(new CreateFunctionAppRequest(
            new CreateFunctionAppRequestBody(
                "revoked-functions",
                "revoked-functions",
                group.Id,
                RevisionRequest(),
                []),
            TestData.TenantId))).RequireSuccess();
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
            (id, ct) => Task.FromResult<Result<DeploymentTargetLookupDto, DeploymentTargetLookupError>>(
                new Ok<DeploymentTargetLookupDto>(target.ToLookupDto())),
            []);
        var loadPlacement = Services.GetRequiredService<LoadExactResourceDeploymentRuntimePlacement>();
        var resolver = new FunctionResourcePlacementResolver(async (tenant, type, resource, deployment, ct) =>
        {
            var placement = await loadPlacement(tenant, type, resource, deployment, ct);
            Assert.NotNull(placement);
            return placement with { Status = "Applied" };
        });
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(
                DbContext.FunctionApps,
                DbContext.FunctionRevisions).InvokeAsync,
            resolver,
            manager,
            Services.GetRequiredService<FailFunctionRevision>(),
            [],
            NullLogger<FunctionRevisionCreatedConsumer>.Instance);
        await consumer.HandleAsync(new FunctionRevisionCreatedEvent(
            created.FunctionAppId,
            created.RevisionId,
            created.RevisionNumber,
            Placement(created),
            null!)
        {
            EventCore = EventCore.Create(TestData.TenantId)
        });
        Assert.Equal(0, provider.Calls);
        var revision = await DbContext.FunctionRevisions
            .Find(item => item.Id == created.RevisionId)
            .SingleAsync();
        Assert.Equal(
            "kubernetes-apply",
            Assert.IsType<FailedFunctionRevisionStatus>(revision.Status).Failure.Code);
    }

    private sealed class NoCallsKubernetesProvider : IKubernetesClientProvider
    {
        public int Calls { get; private set; }
        public Task<IKubernetes> GetAsync(
            string deploymentTargetId,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("A revoked target must not reach Kubernetes.");
        }
        public Task<IKubernetes> GetDefaultAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Default target must not be used.");
    }

    [Fact]
    public async Task Failed_kubernetes_apply_marks_revision_failed_and_redelivery_is_a_noop()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = (await Mediator.Send(new CreateFunctionAppRequest(
            new CreateFunctionAppRequestBody(
                "consumer-functions",
                "consumer-functions",
                group.Id,
                RevisionRequest(),
                []),
            TestData.TenantId))).RequireSuccess();
        var manager = new RecordingKubernetesManager(
            new Err<FunctionRevisionKubernetesOperationError>(
                new FunctionRevisionKubernetesUnavailable("Kubernetes rejected the manifest.")));
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(
                DbContext.FunctionApps,
                DbContext.FunctionRevisions).InvokeAsync,
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
            EventCore = EventCore.Create(TestData.TenantId)
        };

        await consumer.HandleAsync(message);
        await consumer.HandleAsync(message);

        Assert.Equal(1, manager.ApplyCalls);
        var revision = await DbContext.FunctionRevisions
            .Find(candidate => candidate.Id == created.RevisionId)
            .SingleAsync();
        var failed = Assert.IsType<FailedFunctionRevisionStatus>(revision.Status);
        Assert.Equal("kubernetes-apply", failed.Failure.Code);
        var app = await DbContext.FunctionApps
            .Find(candidate => candidate.Id == created.FunctionAppId)
            .SingleAsync();
        Assert.Null(app.PendingRevisionId);
        Assert.Equal(FunctionAppStatus.Failed, app.Status);
    }

    [Fact]
    public async Task Missing_runtime_authentication_fails_closed_after_manifest_apply()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = (await Mediator.Send(new CreateFunctionAppRequest(
            new CreateFunctionAppRequestBody(
                "unconfigured-runtime-functions",
                "unconfigured-runtime-functions",
                group.Id,
                RevisionRequest(),
                []),
            TestData.TenantId))).RequireSuccess();
        var manager = new RecordingKubernetesManager(new Ok<Unit>(Unit.Value));
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(
                DbContext.FunctionApps,
                DbContext.FunctionRevisions).InvokeAsync,
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
            EventCore = EventCore.Create(TestData.TenantId)
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
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = (await Mediator.Send(new CreateFunctionAppRequest(
            new CreateFunctionAppRequestBody(
                "ready-redelivery-functions",
                "ready-redelivery-functions",
                group.Id,
                RevisionRequest(),
                []),
            TestData.TenantId))).RequireSuccess();
        var markReady = Services.GetRequiredService<MarkFunctionRevisionReady>();
        var fail = Services.GetRequiredService<FailFunctionRevision>();
        (await markReady(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).RequireLifecycleSuccess();
        var manager = new RecordingKubernetesManager(new Ok<Unit>(Unit.Value));
        var provisioner = new RecordingProvisioner();
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(
                DbContext.FunctionApps,
                DbContext.FunctionRevisions).InvokeAsync,
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
            EventCore = EventCore.Create(TestData.TenantId)
        };

        await consumer.HandleAsync(message);

        Assert.Equal(0, manager.ApplyCalls);
        Assert.IsType<ReadyFunctionRevisionStatus>(Assert.IsType<FunctionRevision>(provisioner.Revision).Status);
    }

    [Fact]
    public async Task Ready_revision_without_runtime_authentication_is_stopped_before_failure()
    {
        var group = await DbContext.InsertResourceGroupAsync(TestData.SeedResourceGroup());
        var created = (await Mediator.Send(new CreateFunctionAppRequest(
            new CreateFunctionAppRequestBody(
                "ready-unconfigured-functions",
                "ready-unconfigured-functions",
                group.Id,
                RevisionRequest(),
                []),
            TestData.TenantId))).RequireSuccess();
        var markReady = Services.GetRequiredService<MarkFunctionRevisionReady>();
        var fail = Services.GetRequiredService<FailFunctionRevision>();
        (await markReady(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).RequireLifecycleSuccess();
        var manager = new RecordingKubernetesManager(new Ok<Unit>(Unit.Value));
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(
                DbContext.FunctionApps,
                DbContext.FunctionRevisions).InvokeAsync,
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
            EventCore = EventCore.Create(TestData.TenantId)
        };

        await consumer.HandleAsync(message);

        Assert.Equal([0], manager.BootstrapScaleRequests);
        var revision = await DbContext.FunctionRevisions
            .Find(candidate => candidate.Id == created.RevisionId)
            .SingleAsync();
        Assert.IsType<FailedFunctionRevisionStatus>(revision.Status);
    }

    private static FunctionRevisionRequest RevisionRequest() =>
        new(
            new FunctionImageRequest("registry.example", "team/functions", "v1"),
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, string>.Empty),
            new FunctionResources(
                new FunctionResourceValues(100, 128),
                new FunctionResourceValues(500, 512)),
            new FunctionScalingPolicy(0, 5, 4, 8, 60, 30),
            [
                new FunctionHandlerRequest(
                    "webhook",
                    4,
                    new HttpFunctionTriggerRequest("orders", ["POST"], FunctionExposure.Internal))
            ]);

    private static FunctionPlacementSnapshot Placement(CreateFunctionAppResponse created) =>
        new(
            created.Placement.DeploymentId,
            created.Placement.RuntimeCellId,
            created.Placement.DeploymentTargetId);

    private sealed class FixedPlacementResolver : IFunctionResourcePlacementResolver
    {
        public Task<Result<FunctionResourcePlacement, FunctionResourcePlacementUnavailable>> ResolveAsync(
            FunctionApp app,
            bool requireReady = true,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<FunctionResourcePlacement, FunctionResourcePlacementUnavailable>>(
                new Ok<FunctionResourcePlacement>(new FunctionResourcePlacement(
                    app.Placement.ResourceDeploymentId,
                    app.Placement.RuntimeCellId,
                    app.Placement.DeploymentTargetId,
                    "functions",
                    "507f1f77bcf86cd799439044")));
    }

    private sealed class RecordingKubernetesManager(
        Result<Unit, FunctionRevisionKubernetesOperationError> applyResult)
        : IFunctionRevisionKubernetesManager
    {
        public int ApplyCalls { get; private set; }
        public List<int> BootstrapScaleRequests { get; } = [];

        public Task<Result<Unit, FunctionRevisionKubernetesOperationError>> ApplyAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            return Task.FromResult(applyResult);
        }

        public Task<Result<Unit, FunctionRevisionKubernetesOperationError>> DeleteAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<Unit, FunctionRevisionKubernetesOperationError>>(
                new Ok<Unit>(Unit.Value));

        public Task<Result<Unit, FunctionRevisionKubernetesOperationError>> ScaleProvisioningAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            CancellationToken cancellationToken = default)
        {
            BootstrapScaleRequests.Add(replicas);
            return Task.FromResult<Result<Unit, FunctionRevisionKubernetesOperationError>>(
                new Ok<Unit>(Unit.Value));
        }

        public Task<Result<Unit, FunctionRevisionKubernetesOperationError>> ScaleAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            long coordinationGeneration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<FunctionReplicaState, FunctionRevisionKubernetesOperationError>> ReadReplicaStateAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<FunctionReplicaState, FunctionRevisionKubernetesOperationError>>(
                new Ok<FunctionReplicaState>(new FunctionReplicaState(0, 0, 0, 0)));
    }

    private sealed class RecordingProvisioner : IFunctionRevisionProvisioner
    {
        public FunctionRevision? Revision { get; private set; }

        public Task<FunctionRevisionProvisioningOutcome> ProvisionAsync(
            FunctionRevisionInKubernetes subject,
            CancellationToken cancellationToken)
        {
            Revision = subject.Revision;
            return Task.FromResult(FunctionRevisionProvisioningOutcome.Success());
        }
    }
}
