using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Endpoints;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Events;
using Pole.Features.Functions.Runtime.Consumers;
using Pole.Features.Functions.Runtime.Kubernetes;
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
        var create = await Mediator.Send(new CreateFunctionAppRequest(
            new CreateFunctionAppRequestBody(
                "consumer-functions",
                "consumer-functions",
                group.Id,
                RevisionRequest(),
                []),
            TestData.TenantId));
        Assert.True(create.TryGetValue(out var created, out var createFailure), createFailure?.Message);
        var manager = new RecordingKubernetesManager(
            Failures.ExternalService(
                "Kubernetes rejected the manifest.",
                "Kubernetes",
                "invalid manifest"));
        var consumer = new FunctionRevisionCreatedConsumer(
            DbContext,
            manager,
            Services.GetRequiredService<IFunctionRevisionLifecycleService>(),
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
        Assert.Equal(FunctionRevisionState.Failed, revision.State);
        Assert.Equal("kubernetes-apply", revision.Failure?.Code);
        var app = await DbContext.FunctionApps
            .Find(candidate => candidate.Id == created.FunctionAppId)
            .SingleAsync();
        Assert.Null(app.PendingRevisionId);
        Assert.Equal(FunctionAppStatus.Failed, app.Status);
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

        public Task<Result> ScaleAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result>(Result.Success());
    }
}
