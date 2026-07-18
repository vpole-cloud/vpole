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

namespace Pole.Tests.Integration.FunctionApps;

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
            lifecycle,
            NullLogger<FunctionRevisionProvisioner>.Instance);

        var outcome = await provisioner.ProvisionAsync(subject.App, subject.Revision, CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal([0], manager.ScaleRequests);
        Assert.Equal(subject.Revision.Id, lifecycle.PromotedRevisionId);
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
            lifecycle,
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
        public Task<Result<FunctionEndpointSnapshot>> EnsureActiveAsync(
            FunctionApp app,
            FunctionRevision revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<FunctionEndpointSnapshot>>(new FunctionEndpointSnapshot(
                new FunctionServiceKey(subject.Namespace, subject.ServiceName),
                [endpoint],
                DateTimeOffset.UtcNow));
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

        public Task<Result> ApplyAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) => Task.FromResult<Result>(Result.Success());

        public Task<Result> DeleteAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) => Task.FromResult<Result>(Result.Success());

        public Task<Result> ScaleAsync(
            FunctionRevisionInKubernetes revision,
            int replicas,
            CancellationToken cancellationToken = default)
        {
            ScaleRequests.Add(replicas);
            return Task.FromResult<Result>(Result.Success());
        }

        public Task<Result<FunctionReplicaState>> ReadReplicaStateAsync(
            FunctionRevisionInKubernetes revision,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<Result<FunctionReplicaState>>(new FunctionReplicaState(0, 0, 0, 0));
    }

    private sealed class RecordingLifecycle : IFunctionRevisionLifecycleService
    {
        public string? PromotedRevisionId { get; private set; }

        public Task<Result> PromoteAsync(
            string tenantId,
            string functionAppId,
            string revisionId,
            CancellationToken cancellationToken = default)
        {
            PromotedRevisionId = revisionId;
            return Task.FromResult<Result>(Result.Success());
        }

        public Task<Result> FailAsync(
            string tenantId,
            string functionAppId,
            string revisionId,
            FunctionRevisionFailure failure,
            CancellationToken cancellationToken = default) => Task.FromResult<Result>(Result.Success());

        public Task<Result> RetireAsync(
            string tenantId,
            string functionAppId,
            string revisionId,
            CancellationToken cancellationToken = default) => Task.FromResult<Result>(Result.Success());
    }
}
