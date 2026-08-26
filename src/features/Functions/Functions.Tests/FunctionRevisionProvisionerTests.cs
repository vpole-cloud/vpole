using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Activation;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Provisioning;
using Pole.Functions.Protocol;
using Pole.ResultType;
using Xunit;

namespace Pole.Features.Functions.Tests;

public sealed class FunctionRevisionProvisionerTests
{
    [Fact]
    public async Task Valid_agent_metadata_scales_to_minimum_before_promoting_revision()
    {
        var subject = CreateSubject();
        var endpoint = new FunctionEndpoint("10.0.0.1", 8080, "pod-1");
        var manager = new RecordingManager();
        var lifecycle = new RecordingLifecycle();
        var provisioner = new FunctionRevisionProvisioner(
            new ReadyActivator(endpoint, subject),
            new MetadataClient(Metadata(subject, "pod-1")),
            manager,
            lifecycle.MarkReadyAsync,
            lifecycle.PromoteAsync,
            NullLogger<FunctionRevisionProvisioner>.Instance);

        var outcome = await provisioner.ProvisionAsync(subject.App, subject.Revision, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal([0], manager.ScaleRequests);
        Assert.Equal(subject.Revision.Id, lifecycle.MarkedReadyRevisionId);
        Assert.Equal(subject.Revision.Id, lifecycle.PromotedRevisionId);
    }

    [Fact]
    public async Task Ready_revision_resumes_at_promotion_without_repeating_bootstrap_work()
    {
        var provisioning = CreateSubject();
        var subject = provisioning with
        {
            Revision = provisioning.Revision.MarkReady(DateTimeOffset.UtcNow)
        };
        var endpoint = new FunctionEndpoint("10.0.0.1", 8080, "pod-1");
        var manager = new RecordingManager();
        var lifecycle = new RecordingLifecycle();
        var provisioner = new FunctionRevisionProvisioner(
            new ReadyActivator(endpoint, subject),
            new MetadataClient(Metadata(subject, "pod-1")),
            manager,
            lifecycle.MarkReadyAsync,
            lifecycle.PromoteAsync,
            NullLogger<FunctionRevisionProvisioner>.Instance);

        var outcome = await provisioner.ProvisionAsync(subject.App, subject.Revision, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Empty(manager.ScaleRequests);
        Assert.Null(lifecycle.MarkedReadyRevisionId);
        Assert.Equal(subject.Revision.Id, lifecycle.PromotedRevisionId);
    }

    [Fact]
    public async Task Typed_promotion_failure_is_reported_to_the_provisioning_workflow()
    {
        var provisioning = CreateSubject();
        var subject = provisioning with
        {
            Revision = provisioning.Revision.MarkReady(DateTimeOffset.UtcNow)
        };
        var endpoint = new FunctionEndpoint("10.0.0.1", 8080, "pod-1");
        var manager = new RecordingManager();
        var lifecycle = new RecordingLifecycle
        {
            PromoteOutcome = new FunctionRevisionNotReady(subject.Revision.Id)
        };
        var provisioner = new FunctionRevisionProvisioner(
            new ReadyActivator(endpoint, subject),
            new MetadataClient(Metadata(subject, "pod-1")),
            manager,
            lifecycle.MarkReadyAsync,
            lifecycle.PromoteAsync,
            NullLogger<FunctionRevisionProvisioner>.Instance);

        var outcome = await provisioner.ProvisionAsync(subject.App, subject.Revision, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal("promotion", outcome.FailureCode);
        Assert.Equal("Function revision is not ready for promotion.", outcome.FailureMessage);
        Assert.Equal([0], manager.ScaleRequests);
    }

    [Fact]
    public async Task Metadata_identity_mismatch_returns_revision_to_zero_without_promotion()
    {
        var subject = CreateSubject();
        var endpoint = new FunctionEndpoint("10.0.0.1", 8080, "pod-1");
        var manager = new RecordingManager();
        var lifecycle = new RecordingLifecycle();
        var provisioner = new FunctionRevisionProvisioner(
            new ReadyActivator(endpoint, subject),
            new MetadataClient(Metadata(subject, "another-pod")),
            manager,
            lifecycle.MarkReadyAsync,
            lifecycle.PromoteAsync,
            NullLogger<FunctionRevisionProvisioner>.Instance);

        var outcome = await provisioner.ProvisionAsync(subject.App, subject.Revision, CancellationToken.None);

        Assert.False(outcome.Succeeded);
        Assert.Equal("agent-metadata", outcome.FailureCode);
        Assert.Equal([0], manager.ScaleRequests);
        Assert.Null(lifecycle.PromotedRevisionId);
    }

    private static FunctionAgentMetadataResponse Metadata(
        FunctionRevisionInKubernetes subject,
        string instanceId) =>
        new(
            PoleFunctionProtocol.Version,
            "test-runtime",
            instanceId,
            subject.App.Id,
            subject.Revision.Id,
            FunctionAgentLifecycleState.Accepting,
            [new FunctionHandlerMetadata("webhook", FunctionTriggerKind.Http, 4)]);

    private static FunctionRevisionInKubernetes CreateSubject()
    {
        var spec = new FunctionRevisionSpecInput(
            new FunctionImage("registry.example", "team/functions", "v1", null, null),
            PoleFunctionProtocol.Version,
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, string>.Empty),
            new FunctionResources(
                new FunctionResourceValues(100, 128),
                new FunctionResourceValues(500, 512)),
            new FunctionScalingPolicy(0, 5, 4, 8, 30, 30),
            [
                new FunctionHandlerSpec(
                    "webhook",
                    4,
                    new HttpFunctionTriggerSpec(
                        string.Empty,
                        "orders",
                        ["POST"],
                        FunctionExposure.Public))
            ]);
        var created = FunctionAppFactory.Create(
            "tenant-1",
            "group-1",
            "functions",
            "orders",
            "orders",
            spec,
            [],
            DateTimeOffset.UtcNow);
        var app = created.FunctionApp with { Id = "507f1f77bcf86cd799439011" };
        var revision = created.Revision with
        {
            Id = "507f1f77bcf86cd799439012",
            FunctionAppId = app.Id
        };
        return new FunctionRevisionInKubernetes(app, revision);
    }

    private sealed class ReadyActivator(
        FunctionEndpoint endpoint,
        FunctionRevisionInKubernetes subject) : IFunctionRevisionActivator
    {
        public Task<Result<FunctionEndpointSnapshot, FunctionRevisionActivationError>> EnsureActiveAsync(
            FunctionApp app,
            FunctionRevision revision,
            CancellationToken cancellationToken = default,
            bool forceScaleFence = false) =>
            Task.FromResult<Result<FunctionEndpointSnapshot, FunctionRevisionActivationError>>(
                new Ok<FunctionEndpointSnapshot>(new FunctionEndpointSnapshot(
                new FunctionServiceKey(subject.Namespace, subject.ServiceName),
                [endpoint],
                DateTimeOffset.UtcNow)));
    }

    private sealed class MetadataClient(FunctionAgentMetadataResponse metadata) : IFunctionAgentClient
    {
        public Task<FunctionAgentProtocolResponse<FunctionAgentMetadataResponse>> GetMetadataAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            CancellationToken cancellationToken) =>
            Task.FromResult(new FunctionAgentProtocolResponse<FunctionAgentMetadataResponse>(
                HttpStatusCode.OK,
                metadata,
                null));

        public Task<FunctionAgentProtocolResponse<FunctionAgentStatusResponse>> GetStatusAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FunctionAgentProtocolResponse<ReserveFunctionAttemptResponse>> ReserveAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            ReserveFunctionAttemptRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FunctionAgentProtocolResponse<StartFunctionAttemptResponse>> ExecuteAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            string attemptId,
            ExecuteFunctionAttemptRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FunctionAgentProtocolResponse<FunctionAttemptStatusResponse>> ObserveAsync(
            FunctionEndpoint endpoint,
            FunctionApp app,
            FunctionRevision revision,
            string attemptId,
            int waitUpToMilliseconds,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingManager : IFunctionRevisionKubernetesManager
    {
        public List<int> ScaleRequests { get; } = [];

        public Task<Result<Unit, FunctionRevisionKubernetesOperationError>> ApplyAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) => Task.FromResult<Result<Unit, FunctionRevisionKubernetesOperationError>>(
                new Ok<Unit>(Unit.Value));

        public Task<Result<Unit, FunctionRevisionKubernetesOperationError>> DeleteAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) => Task.FromResult<Result<Unit, FunctionRevisionKubernetesOperationError>>(
                new Ok<Unit>(Unit.Value));

        public Task<Result<Unit, FunctionRevisionKubernetesOperationError>> ScaleProvisioningAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            CancellationToken cancellationToken = default)
        {
            ScaleRequests.Add(replicas);
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

    private sealed class RecordingLifecycle
    {
        public string? MarkedReadyRevisionId { get; private set; }
        public string? PromotedRevisionId { get; private set; }
        public FunctionRevisionLifecycleOutcome PromoteOutcome { get; init; } =
            new FunctionRevisionLifecycleCompleted();

        public Task<FunctionRevisionLifecycleOutcome> MarkReadyAsync(
            string tenantId,
            string functionAppId,
            string revisionId,
            CancellationToken cancellationToken = default)
        {
            MarkedReadyRevisionId = revisionId;
            return Task.FromResult<FunctionRevisionLifecycleOutcome>(
                new FunctionRevisionLifecycleCompleted());
        }

        public Task<FunctionRevisionLifecycleOutcome> PromoteAsync(
            string tenantId,
            string functionAppId,
            string revisionId,
            CancellationToken cancellationToken = default)
        {
            PromotedRevisionId = revisionId;
            return Task.FromResult(PromoteOutcome);
        }
    }
}
