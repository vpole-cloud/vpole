using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Endpoints;
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
            Failures.ExternalService(
                "Kubernetes rejected the manifest.",
                "Kubernetes",
                "invalid manifest"));
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(
                DbContext.FunctionApps,
                DbContext.FunctionRevisions).InvokeAsync,
            manager,
            Services.GetRequiredService<IFunctionRevisionLifecycleService>(),
            [],
            NullLogger<FunctionRevisionCreatedConsumer>.Instance);
        var message = new FunctionRevisionCreatedEvent(
            created.FunctionAppId,
            created.RevisionId,
            created.RevisionNumber,
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
        var manager = new RecordingKubernetesManager(Result.Success());
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(
                DbContext.FunctionApps,
                DbContext.FunctionRevisions).InvokeAsync,
            manager,
            Services.GetRequiredService<IFunctionRevisionLifecycleService>(),
            [],
            NullLogger<FunctionRevisionCreatedConsumer>.Instance);
        var message = new FunctionRevisionCreatedEvent(
            created.FunctionAppId,
            created.RevisionId,
            created.RevisionNumber,
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
        var lifecycle = Services.GetRequiredService<IFunctionRevisionLifecycleService>();
        (await lifecycle.MarkReadyAsync(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).RequireLifecycleSuccess();
        var manager = new RecordingKubernetesManager(Result.Success());
        var provisioner = new RecordingProvisioner();
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(
                DbContext.FunctionApps,
                DbContext.FunctionRevisions).InvokeAsync,
            manager,
            lifecycle,
            [provisioner],
            NullLogger<FunctionRevisionCreatedConsumer>.Instance);
        var message = new FunctionRevisionCreatedEvent(
            created.FunctionAppId,
            created.RevisionId,
            created.RevisionNumber,
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
        var lifecycle = Services.GetRequiredService<IFunctionRevisionLifecycleService>();
        (await lifecycle.MarkReadyAsync(
            TestData.TenantId,
            created.FunctionAppId,
            created.RevisionId)).RequireLifecycleSuccess();
        var manager = new RecordingKubernetesManager(Result.Success());
        var consumer = new FunctionRevisionCreatedConsumer(
            new MongoLoadFunctionRevisionProvisioningSubject(
                DbContext.FunctionApps,
                DbContext.FunctionRevisions).InvokeAsync,
            manager,
            lifecycle,
            [],
            NullLogger<FunctionRevisionCreatedConsumer>.Instance);
        var message = new FunctionRevisionCreatedEvent(
            created.FunctionAppId,
            created.RevisionId,
            created.RevisionNumber,
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

    private sealed class RecordingKubernetesManager(Result applyResult)
        : IFunctionRevisionKubernetesManager
    {
        public int ApplyCalls { get; private set; }
        public List<int> BootstrapScaleRequests { get; } = [];

        public Task<Result> ApplyAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default)
        {
            ApplyCalls++;
            return Task.FromResult(applyResult);
        }

        public Task<Result> DeleteAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(Result.Success());

        public Task<Result> ScaleProvisioningAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            CancellationToken cancellationToken = default)
        {
            BootstrapScaleRequests.Add(replicas);
            return Task.FromResult<Result>(Result.Success());
        }

        public Task<Result> ScaleAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            long coordinationGeneration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<FunctionReplicaState>> ReadReplicaStateAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<FunctionReplicaState>>(new FunctionReplicaState(0, 0, 0, 0));
    }

    private sealed class RecordingProvisioner : IFunctionRevisionProvisioner
    {
        public FunctionRevision? Revision { get; private set; }

        public Task<FunctionRevisionProvisioningOutcome> ProvisionAsync(
            FunctionApp app,
            FunctionRevision revision,
            CancellationToken cancellationToken)
        {
            Revision = revision;
            return Task.FromResult(FunctionRevisionProvisioningOutcome.Success());
        }
    }
}
