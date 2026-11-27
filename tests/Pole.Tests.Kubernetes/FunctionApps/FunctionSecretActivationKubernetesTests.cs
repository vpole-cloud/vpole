// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using k8s;
using k8s.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Activation;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Coordination;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Provisioning;
using Pole.Features.Functions.Runtime.Security;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceGroups.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Functions.Protocol;
using Pole.Testing;
using Pole.Testing.Kubernetes;
using Pole.Workloads;
using Xunit;

namespace Pole.Tests.Kubernetes.FunctionApps;

[Trait("Layer", "Kubernetes")]
public sealed class FunctionSecretActivationKubernetesTests(
    KubernetesClusterFixture fixture
) : KubernetesIntegrationTestBase(fixture)
{
    [KubernetesFact("harbor", "buildkit")]
    public async Task Logical_secret_projection_enables_authenticated_sdk_activation_and_invocation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var ct = timeout.Token;
        await using var image = await FunctionSdkTestImage.CreateAsync(
            new SelectedKubernetesLab(fixture.State, Target),
            fixture.Identity,
            ct);
        var subject = CreateSubject(image);
        var secret = SecretFactory.Create(
            "507f1f77bcf86cd799439051",
            subject.App.TenantId,
            subject.App.ResourceGroupId,
            subject.Placement.IsolationScopeId,
            "activation-password",
            "live SDK projection",
            ["password"],
            DateTime.UtcNow);
        var binding = SecretBinding.Create(
            "507f1f77bcf86cd799439052",
            FunctionResourceDeployment.ResourceType.Value,
            subject.App.Id,
            new SelectedSecretDeployments([subject.Placement.ResourceDeploymentId]),
            ImmutableDictionary<string, string>.Empty.Add("password", "sdk-password"));
        secret = secret.Bind(binding, DateTime.UtcNow);
        var projection = SecretProjectionFactory.Create(
            secret,
            binding,
            subject.Placement.ResourceDeploymentId,
            subject.Placement.RuntimeCellId,
            subject.Placement.DeploymentTargetId,
            NamespaceName);
        var projectionReady = false;
        using var scaleConflicts = new ScaleConflictHandler(async cancellationToken =>
        {
            var patch = new V1Patch(
                """{"metadata":{"annotations":{"pole.test/scale-conflict":"injected"}}}""",
                V1Patch.PatchType.MergePatch);
            await Kubernetes.AppsV1.PatchNamespacedDeploymentAsync(
                patch,
                subject.DeploymentName,
                NamespaceName,
                cancellationToken: cancellationToken);
        });
        using var runtimeKubernetes = new k8s.Kubernetes(Target.CreateConfiguration(), scaleConflicts);
        var services = new ServiceCollection().AddSecretsFeature();
        services.AddSingleton<IKubernetesClientProvider>(new FixedKubernetesClientProvider(runtimeKubernetes));
        services.AddSingleton<LoadSecretById>((
            tenant, id,
            _) => Task.FromResult(tenant == secret.TenantId && id == secret.Id ? secret : null));
        services.AddSingleton<LoadExactResourceDeploymentRuntimePlacement>((request, _) => Task.FromResult(
            request.TenantId == subject.App.TenantId &&
            request.ResourceType == FunctionResourceDeployment.ResourceType && request.ResourceId == subject.App.Id &&
            request.DeploymentId == subject.Placement.ResourceDeploymentId
                ? RuntimePlacement(subject)
                : null));
        // Mongo tests cover the resolver's admission/fencing. This fixture supplies its current projection state.
        services.AddSingleton<EnsureConsumerSecretProjections>((request, _) =>
        {
            Assert.Equal(subject.App.TenantId, request.TenantId);
            Assert.Equal(FunctionResourceDeployment.ResourceType, request.ConsumerType);
            Assert.Equal(subject.App.Id, request.ConsumerId);
            Assert.Equal(subject.Placement.ResourceDeploymentId, request.DeploymentId);
            Assert.Equal(new ConsumerSecretKeyReference(secret.Id, "password"), Assert.Single(request.References));
            return Task.FromResult<EnsureConsumerSecretProjectionsOutcome>(
                projectionReady
                    ? new ConsumerSecretProjectionKeys(
                    [
                        new ConsumerSecretProjectionKey(
                            secret.Id,
                            "password",
                            projection.KubernetesName,
                            binding.ProjectedKey("password"),
                            projection.DesiredVersion),
                    ])
                    : new ConsumerSecretProjectionPending(secret.Id));
        });
        await using var provider = services.BuildServiceProvider();
        using var signingKey = RSA.Create(2048);
        var issuer = new FunctionAgentTokenIssuer(
            new FunctionAgentTokenOptions(),
            new SigningKeyProvider(new FunctionAgentSigningKey("live-sdk", signingKey)),
            TimeProvider.System);
        var manager = new FunctionRevisionKubernetesManager(
            provider.GetRequiredService<IKubernetesClientProvider>(),
            new TestDeploymentTargetLookup().LookupAsync,
            (_, _) => Task.FromResult<EnsureRuntimeCellIngressBaselineOutcome>(new RuntimeCellIngressBaselineReady()),
            [issuer],
            provider.GetRequiredService<IServiceScopeFactory>());

        try
        {
            var unavailable = Assert.IsAssignableFrom<FunctionRevisionKubernetesOperationError>(
                (await manager.ApplyAsync(subject, ct)).Value);
            Assert.IsType<FunctionRevisionSecretsUnavailable>(unavailable.Value);
            Assert.Empty(
                (await Kubernetes.AppsV1.ListNamespacedDeploymentAsync(NamespaceName, cancellationToken: ct)).Items);

            var target = provider.GetRequiredService<ISecretProjectionTarget>();
            var projected = await target.ApplyAsync(
                projection,
                _ => Task.FromResult<SecretProjectionManifestOutcome>(
                    SecretProjectionManifest.Create(
                        projection,
                        binding,
                        new Dictionary<string, string> { ["password"] = Guid.NewGuid().ToString("N") })),
                ct);
            Assert.IsType<SecretProjectionTargetSucceeded>(projected.Value);
            projectionReady = true;
            Assert.IsType<FunctionRevisionKubernetesMutationCompleted>((await manager.ApplyAsync(subject, ct)).Value);
            var deployment = await Kubernetes.AppsV1.ReadNamespacedDeploymentAsync(
                subject.DeploymentName,
                NamespaceName,
                cancellationToken: ct);
            Assert.Equal(0, deployment.Spec.Replicas);
            var reference = Assert.Single(
                    deployment.Spec.Template.Spec.Containers.Single().Env,
                    item => item.Name == "STAGE_FIVE_SECRET")
                .ValueFrom.SecretKeyRef;
            Assert.Equal(projection.KubernetesName, reference.Name);
            Assert.Equal(binding.ProjectedKey("password"), reference.Key);

            var scaled = await manager.ScaleProvisioningAsync(subject, 1, ct);
            Assert.True(
                scaled is FunctionRevisionKubernetesMutationCompleted,
                $"Provisioning scale failed; Kubernetes scale statuses: {string.Join(", ", scaleConflicts.Statuses)}.");
            Assert.Contains(HttpStatusCode.Conflict, scaleConflicts.Statuses);
            var registry = new FunctionEndpointRegistry(TimeProvider.System);
            var endpoint = await WaitForEndpointAsync(subject, registry, ct);
            var pods = await Kubernetes.CoreV1.ListNamespacedPodAsync(NamespaceName, cancellationToken: ct);
            var pod = Assert.Single(pods.Items, item => item.Metadata.Uid == endpoint.TargetUid);
            await using var forward = await KubernetesPortForwardSession.StartAsync(
                Target,
                NamespaceName,
                $"pod/{pod.Metadata.Name}",
                FunctionRevisionInKubernetes.AgentPort,
                ct);
            using var http = new HttpClient(new ForwardedPodTransport(endpoint, forward.LocalPort));
            var client = new FunctionAgentClient(http, issuer);
            var currentRevision = subject.Revision;
            var provisioner = new FunctionRevisionProvisioner(
                new FunctionRevisionActivator(registry, new AlreadyReadyCoordinator(), manager, TimeProvider.System),
                client,
                manager,
                (
                    tenant, app,
                    revision, _) =>
                {
                    Assert.Equal((subject.App.TenantId, subject.App.Id, subject.Revision.Id), (tenant, app, revision));
                    currentRevision = currentRevision.MarkReady(DateTimeOffset.UtcNow);
                    return Task.FromResult<FunctionRevisionLifecycleOutcome>(new FunctionRevisionLifecycleCompleted());
                },
                (
                    tenant, app,
                    revision, _) =>
                {
                    Assert.Equal((subject.App.TenantId, subject.App.Id, subject.Revision.Id), (tenant, app, revision));
                    currentRevision = currentRevision.Activate();
                    return Task.FromResult<FunctionRevisionLifecycleOutcome>(new FunctionRevisionLifecycleCompleted());
                },
                NullLogger<FunctionRevisionProvisioner>.Instance);
            Assert.IsType<FunctionRevisionProvisioningCompleted>((await provisioner.ProvisionAsync(subject, ct)).Value);
            Assert.IsType<ActiveFunctionRevisionStatus>(currentRevision.Status);

            var wrongPod = await client.GetMetadataAsync(
                endpoint with { TargetUid = Guid.NewGuid().ToString("N") },
                subject.App,
                subject.Revision,
                ct);
            Assert.False(wrongPod.IsSuccess);
            Assert.Contains(wrongPod.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
            await InvokeAsync(client, endpoint, subject, ct);
        }
        finally
        {
            // Delete the workload before removing its private build fixture and registry project.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await manager.DeleteAsync(subject, cleanup.Token);
            while ((await Kubernetes.CoreV1.ListNamespacedPodAsync(NamespaceName, cancellationToken: cleanup.Token))
                   .Items.Count > 0)
                await Task.Delay(500, cleanup.Token);
        }
    }

    private async Task<FunctionEndpoint> WaitForEndpointAsync(
        FunctionRevisionInKubernetes subject,
        FunctionEndpointRegistry registry,
        CancellationToken ct)
    {
        var key = new FunctionServiceKey(subject.Placement.DeploymentTargetId, NamespaceName, subject.ServiceName);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var slices = await Kubernetes.DiscoveryV1.ListNamespacedEndpointSliceAsync(
                    NamespaceName,
                    cancellationToken: ct);
                registry.ReplaceTarget(subject.Placement.DeploymentTargetId, slices.Items);
                var snapshot = registry.GetSnapshot(key);
                if (snapshot.HasReadyEndpoints)
                    return Assert.Single(snapshot.Endpoints);

                await Task.Delay(500, ct);
            }
        }
        catch (OperationCanceledException exception) when (ct.IsCancellationRequested)
        {
            throw await CreateTimeoutExceptionAsync("Function SDK endpoint did not become ready.", exception);
        }

        throw await CreateTimeoutExceptionAsync("Function SDK endpoint did not become ready.");
    }

    private static async Task InvokeAsync(
        FunctionAgentClient client,
        FunctionEndpoint endpoint,
        FunctionRevisionInKubernetes subject,
        CancellationToken ct)
    {
        var attempt = Guid.NewGuid().ToString("N");
        var identity = new FunctionAttemptIdentity(
            subject.App.TenantId,
            subject.App.Id,
            subject.Revision.Id,
            "hello",
            FunctionTriggerKind.Http,
            Guid.NewGuid().ToString("N"),
            attempt,
            1);
        var reserved = await client.ReserveAsync(
            endpoint,
            subject.App,
            subject.Revision,
            new ReserveFunctionAttemptRequest(
                PoleFunctionProtocol.Version,
                identity,
                DateTimeOffset.UtcNow.AddSeconds(30),
                new FunctionInvocationTraceContext(null, null, Guid.NewGuid().ToString("N"))),
            ct);
        Assert.True(reserved.IsSuccess);
        var started = await client.ExecuteAsync(
            endpoint,
            subject.App,
            subject.Revision,
            attempt,
            new ExecuteFunctionAttemptRequest(
                PoleFunctionProtocol.Version,
                reserved.Value!.ReservationToken!,
                new HttpFunctionInvocationPayload("GET", "/hello", "", [], [])),
            ct);
        Assert.True(started.IsSuccess);
        var observed = await client.ObserveAsync(endpoint, subject.App, subject.Revision, attempt, 5000, ct);
        Assert.True(observed.IsSuccess);
        var completed = Assert.IsType<CompletedFunctionAttemptStatus>(observed.Value!.Status);
        Assert.Equal(FunctionAttemptOutcome.Succeeded, completed.Outcome);
        var response = Assert.IsType<HttpFunctionInvocationResult>(completed.Result);
        Assert.Equal(200, response.StatusCode);
        Assert.NotNull(response.Body);
        Assert.Equal("configured", Encoding.UTF8.GetString(response.Body));
    }

    private FunctionRevisionInKubernetes CreateSubject(FunctionSdkTestImage image)
    {
        var input = new FunctionRevisionSpecInput(
            new FunctionImage(image.RegistryHost, image.Repository, image.Tag, null, null),
            PoleFunctionProtocol.Version,
            new FunctionConfiguration(
                ImmutableDictionary<string, string>.Empty,
                ImmutableDictionary<string, FunctionSecretReference>.Empty.Add(
                    "STAGE_FIVE_SECRET",
                    new FunctionSecretReference("507f1f77bcf86cd799439051", "password"))),
            new FunctionResources(new FunctionResourceValues(100, 256), new FunctionResourceValues(500, 512)),
            new FunctionScalingPolicy(1, 1, 1, 10, 60, 120),
            [
                new FunctionHandlerSpec(
                    "hello",
                    10,
                    new HttpFunctionTriggerSpec("", "hello", ["GET"], FunctionExposure.Internal)),
            ]);
        var created = FunctionAppFactory.Create(
            "tenant-live",
            "507f1f77bcf86cd799439043",
            new FunctionPlacementIdentity(
                "507f1f77bcf86cd799439041",
                "507f1f77bcf86cd799439042",
                TestPlatformDefaults.DeploymentTargetId),
            "function-live",
            "function-live",
            input,
            [],
            DateTimeOffset.UtcNow);
        return new FunctionRevisionInKubernetes(
            created.FunctionApp,
            created.Revision,
            new FunctionResourcePlacement(
                created.FunctionApp.Placement.ResourceDeploymentId,
                created.FunctionApp.Placement.RuntimeCellId,
                TestPlatformDefaults.DeploymentTargetId,
                NamespaceName,
                "507f1f77bcf86cd799439044"));
    }

    private static ResourceDeploymentRuntimePlacementDto RuntimePlacement(FunctionRevisionInKubernetes subject) => new(
        subject.Placement.ResourceDeploymentId,
        subject.App.Id,
        subject.Placement.RuntimeCellId,
        subject.Placement.DeploymentTargetId,
        subject.Placement.KubernetesNamespace,
        1,
        1,
        new ResourceDeploymentComputeConfiguration(
            "507f1f77bcf86cd799439045",
            1,
            subject.Placement.DeploymentTargetId,
            "live",
            new ResourceDeploymentComputeResourceValues(100, 256),
            new ResourceDeploymentComputeResourceValues(500, 512),
            ComputeArchitecture.Amd64,
            [],
            [],
            [],
            [],
            [],
            null),
        ResourceDeploymentStatus.Pending,
        ResourceDeploymentDesiredOperation.Apply,
        subject.Placement.IsolationScopeId);

    private sealed class ScaleConflictHandler(
        Func<CancellationToken, Task> supersedeScale
    ) : DelegatingHandler
    {
        private int injected;
        public List<HttpStatusCode> Statuses { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (!request.RequestUri!.AbsolutePath.EndsWith("/scale", StringComparison.Ordinal))
                return response;

            Statuses.Add(response.StatusCode);
            if (request.Method == HttpMethod.Get && response.IsSuccessStatusCode &&
                Interlocked.Exchange(ref injected, 1) == 0)
                // A real write invalidates the returned scale resourceVersion before the manager's PUT.
                // Only status codes are recorded; request/response bodies and credentials stay private.
                await supersedeScale(ct);

            return response;
        }
    }

    private sealed record SigningKeyProvider(
        FunctionAgentSigningKey Key
    ) : IFunctionAgentSigningKeyProvider
    {
        public FunctionAgentSigningKey GetSigningKey() => Key;
    }

    private sealed class ForwardedPodTransport(
        FunctionEndpoint observed,
        int localPort
    ) : DelegatingHandler(new SocketsHttpHandler())
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(observed.Address, request.RequestUri!.Host);
            // Only transport changes: JWT target identity remains the observed Kubernetes Pod UID.
            request.RequestUri = new UriBuilder(request.RequestUri) { Host = "127.0.0.1", Port = localPort }.Uri;
            return base.SendAsync(request, ct);
        }
    }

    private sealed class AlreadyReadyCoordinator : IFunctionActivationCoordinator
    {
        public Task<FunctionActivationBeginDecision> BeginActivationAsync(
            FunctionRevisionCoordinationKey key,
            FunctionActivationBeginRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A ready SDK endpoint must not request another activation.");

        public Task<FunctionActivationCompleteDecision> CompleteActivationAsync(
            FunctionRevisionCoordinationKey key,
            FunctionActivationCompleteRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A ready SDK endpoint has no pending activation lease.");
    }
}
