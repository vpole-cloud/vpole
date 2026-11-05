using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Mongo;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Activation;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Provisioning;
using Pole.Functions.Protocol;
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

        var outcome = await provisioner.ProvisionAsync(subject, CancellationToken.None);

        Assert.IsType<FunctionRevisionProvisioningCompleted>(outcome.Value);
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

        var outcome = await provisioner.ProvisionAsync(subject, CancellationToken.None);

        Assert.IsType<FunctionRevisionProvisioningCompleted>(outcome.Value);
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

        var outcome = await provisioner.ProvisionAsync(subject, CancellationToken.None);

        var failed = Assert.IsType<FunctionRevisionProvisioningFailed>(outcome.Value);
        Assert.Equal("promotion", failed.Code);
        Assert.Equal("Function revision is not ready for promotion.", failed.Message);
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

        var outcome = await provisioner.ProvisionAsync(subject, CancellationToken.None);

        var failed = Assert.IsType<FunctionRevisionProvisioningFailed>(outcome.Value);
        Assert.Equal("agent-metadata", failed.Code);
        Assert.Equal([0], manager.ScaleRequests);
        Assert.Null(lifecycle.PromotedRevisionId);
    }

    [Fact]
    public async Task Gateway_source_restoration_resumes_activation_without_failing_revision()
    {
        var subject = CreateSubject();
        var endpoint = new FunctionEndpoint("10.0.0.1", 8080, "pod-1");
        var activator = new RecoveringGatewayActivator(endpoint, subject);
        var manager = new RecordingManager();
        var lifecycle = new RecordingLifecycle();
        var provisioner = new FunctionRevisionProvisioner(
            activator,
            new MetadataClient(Metadata(subject, "pod-1")),
            manager,
            lifecycle.MarkReadyAsync,
            lifecycle.PromoteAsync,
            NullLogger<FunctionRevisionProvisioner>.Instance);

        var deferred = await provisioner.ProvisionAsync(subject, CancellationToken.None);
        Assert.IsType<FunctionRevisionProvisioningDeferred>(deferred.Value);
        Assert.Empty(manager.ScaleRequests);
        Assert.Null(lifecycle.MarkedReadyRevisionId);

        activator.GatewaySourceAvailable = true;
        var recovered = await provisioner.ProvisionAsync(subject, CancellationToken.None);

        Assert.IsType<FunctionRevisionProvisioningCompleted>(recovered.Value);
        Assert.Equal(subject.Revision.Id, lifecycle.MarkedReadyRevisionId);
        Assert.Equal(subject.Revision.Id, lifecycle.PromotedRevisionId);
    }

    [Fact]
    public async Task Gateway_source_missing_at_minimum_scale_defers_without_stopping_revision()
    {
        var subject = CreateSubject();
        var endpoint = new FunctionEndpoint("10.0.0.1", 8080, "pod-1");
        var manager = new RecordingManager
        {
            ScaleResult = new FunctionRevisionKubernetesOperationError(
                new FunctionRevisionGatewaySourceUnavailable(
                    "The Function gateway source is not configured."))
        };
        var lifecycle = new RecordingLifecycle();
        var provisioner = new FunctionRevisionProvisioner(
            new ReadyActivator(endpoint, subject),
            new MetadataClient(Metadata(subject, "pod-1")),
            manager,
            lifecycle.MarkReadyAsync,
            lifecycle.PromoteAsync,
            NullLogger<FunctionRevisionProvisioner>.Instance);

        var deferred = await provisioner.ProvisionAsync(subject, CancellationToken.None);

        Assert.IsType<FunctionRevisionProvisioningDeferred>(deferred.Value);
        Assert.Single(manager.ScaleRequests);
        Assert.Null(lifecycle.MarkedReadyRevisionId);
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
                ImmutableDictionary<string, FunctionSecretReference>.Empty),
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
            new FunctionPlacementIdentity(
                "507f1f77bcf86cd799439091",
                "507f1f77bcf86cd799439092",
                "507f1f77bcf86cd799439093"),
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
        return new FunctionRevisionInKubernetes(
            app,
            revision,
            new FunctionResourcePlacement(
                app.Placement.ResourceDeploymentId,
                app.Placement.RuntimeCellId,
                app.Placement.DeploymentTargetId,
                "functions",
                "507f1f77bcf86cd799439044"));
    }

    private sealed class ReadyActivator(
        FunctionEndpoint endpoint,
        FunctionRevisionInKubernetes subject) : IFunctionRevisionActivator
    {
        public Task<FunctionRevisionActivationOutcome> EnsureActiveAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default,
            bool forceScaleFence = false) =>
            Task.FromResult<FunctionRevisionActivationOutcome>(
                new FunctionEndpointSnapshot(
                new FunctionServiceKey(
                    subject.Placement.DeploymentTargetId,
                    subject.Namespace,
                    subject.ServiceName),
                [endpoint],
                DateTimeOffset.UtcNow));
    }

    private sealed class RecoveringGatewayActivator(
        FunctionEndpoint endpoint,
        FunctionRevisionInKubernetes subject) : IFunctionRevisionActivator
    {
        public bool GatewaySourceAvailable { get; set; }

        public Task<FunctionRevisionActivationOutcome> EnsureActiveAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default,
            bool forceScaleFence = false) =>
            Task.FromResult<FunctionRevisionActivationOutcome>(GatewaySourceAvailable
                ? new FunctionEndpointSnapshot(
                    new FunctionServiceKey(
                        subject.Placement.DeploymentTargetId,
                        subject.Namespace,
                        subject.ServiceName),
                    [endpoint],
                    DateTimeOffset.UtcNow)
                : new FunctionRevisionActivationError(
                    new FunctionActivationScaleRejected(
                        new FunctionRevisionGatewaySourceUnavailable(
                            "The Function gateway source is not configured."))));
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
        public FunctionRevisionKubernetesMutationOutcome ScaleResult { get; set; } =
            new FunctionRevisionKubernetesMutationCompleted();

        public Task<FunctionRevisionKubernetesMutationOutcome> ApplyAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) => Task.FromResult<FunctionRevisionKubernetesMutationOutcome>(
                new FunctionRevisionKubernetesMutationCompleted());

        public Task<FunctionRevisionKubernetesMutationOutcome> DeleteAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) => Task.FromResult<FunctionRevisionKubernetesMutationOutcome>(
                new FunctionRevisionKubernetesMutationCompleted());

        public Task<FunctionRevisionKubernetesMutationOutcome> ScaleProvisioningAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            CancellationToken cancellationToken = default)
        {
            ScaleRequests.Add(replicas);
            return Task.FromResult(ScaleResult);
        }

        public Task<FunctionRevisionKubernetesMutationOutcome> ScaleAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            long coordinationGeneration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FunctionReplicaStateOutcome> ReadReplicaStateAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<FunctionReplicaStateOutcome>(
                new FunctionReplicaState(0, 0, 0, 0));
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
