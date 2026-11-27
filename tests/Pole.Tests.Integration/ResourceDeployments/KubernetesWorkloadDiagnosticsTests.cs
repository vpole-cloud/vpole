// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using System.Text;
using System.Text.Json;
using k8s;
using k8s.Models;
using Pole.Features.Core.Kubernetes;
using Xunit;

namespace Pole.Tests.Integration.ResourceDeployments;

public sealed class KubernetesWorkloadDiagnosticsTests
{
    private static readonly WorkloadIdentityLabels Identity = new(
        "tenant",
        "group",
        "scope",
        "cell",
        "target",
        "Test",
        "resource",
        "deployment");

    private static V1ObjectMeta Metadata(
        string name,
        string uid,
        string? owner = null) => new()
    {
        Name = name,
        Uid = uid,
        NamespaceProperty = "owned",
        Labels = Identity.ToDictionary(),
        Annotations = new Dictionary<string, string>
            { [KubernetesWorkloadObservation.TemplateGenerationAnnotation] = "1" },
        OwnerReferences = owner is null
            ? []
            :
            [
                new V1OwnerReference
                {
                    Name = "controller", Kind = "StatefulSet", ApiVersion = "apps/v1", Uid = owner, Controller = true,
                },
            ],
    };

    private static V1Pod Pod(string uid = "pod-uid") => new()
    {
        Metadata = Metadata("database-0", uid, "controller-uid"),
        Spec = new V1PodSpec
        {
            Containers =
            [
                new V1Container
                {
                    Name = "app", Image = "private-image-canary",
                    Env = [new V1EnvVar { Name = "PRIVATE", Value = "private-env-canary" }],
                },
                new V1Container { Name = "sidecar", Image = "private-image-canary" },
            ],
            InitContainers = [new V1Container { Name = "init", Image = "private-image-canary" }],
            EphemeralContainers =
                [new V1EphemeralContainer { Name = "exec-private-canary", Image = "private-image-canary" }],
        },
        Status = new V1PodStatus
        {
            Phase = "Pending",
            ContainerStatuses =
            [
                new V1ContainerStatus
                {
                    Name = "app", Ready = false, RestartCount = 3, Image = "private-image-canary",
                    ImageID = "private-image-canary",
                    State = new V1ContainerState
                    {
                        Waiting = new V1ContainerStateWaiting
                            { Reason = "CrashLoopBackOff", Message = "private-native-message-canary" },
                    },
                    LastState = new V1ContainerState { Terminated = new V1ContainerStateTerminated { ExitCode = 1 } },
                },
            ],
        },
    };

    private static Task<OwnedKubernetesWorkload?> Resolve(IKubernetes client, CancellationToken ct) =>
        KubernetesWorkloadDiagnostics.StatefulSetAsync(
            client,
            "database",
            "owned",
            Identity,
            2,
            KubernetesWorkloadObservation.TemplateGenerationAnnotation,
            false,
            ct);

    [Fact]
    public async Task Discovery_includes_owned_failing_old_instances_and_only_safe_bounded_diagnostics()
    {
        var own = Pod();
        var foreign = Pod("foreign-uid");
        foreign.Metadata.Labels["pole.io/tenant-id"] = "foreign";
        var wrong = Pod("wrong-uid");
        wrong.Metadata.OwnerReferences[0].Uid = "wrong-controller";
        using var handler = new Handler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/database", StringComparison.Ordinal)
                ?
                KubernetesJson.Serialize(new V1StatefulSet { Metadata = Metadata("database", "controller-uid") })
                : request.RequestUri.AbsolutePath.EndsWith("/pods", StringComparison.Ordinal)
                    ? KubernetesJson.Serialize(
                        new V1PodList
                        {
                            Metadata = new V1ListMeta { ContinueProperty = "private-native-cursor-canary" },
                            Items = [own, foreign, wrong],
                        })
                    : KubernetesJson.Serialize(
                        new Corev1EventList
                        {
                            Metadata = new V1ListMeta { ContinueProperty = "private-native-cursor-canary" },
                            Items =
                            [
                                new Corev1Event
                                {
                                    InvolvedObject = new V1ObjectReference { Uid = "pod-uid" }, Type = "Warning",
                                    Reason = "private-reason-canary", Message = "private-native-message-canary",
                                    Count = 4,
                                },
                            ],
                        }));
        using var client = Client(handler);
        var result = Assert.IsType<AvailableWorkloadDiagnostics>(
            await KubernetesWorkloadDiagnostics.DiscoverAsync(
                await Resolve(client, CancellationToken.None),
                CancellationToken.None));
        var instance = Assert.Single(result.Instances);
        Assert.Equal("pod-uid", instance.Uid);
        Assert.False(instance.Ready);
        Assert.False(instance.CurrentGeneration);
        Assert.Equal("CrashLoopBackOff", Assert.Single(instance.Reasons));
        Assert.Equal(new[] { "app", "sidecar", "init" }, instance.Containers.Select(container => container.Name));
        Assert.True(instance.Containers[0].PreviousAvailable);
        Assert.True(result.InstancesTruncated);
        Assert.True(result.EventsTruncated);
        var warning = Assert.Single(result.Events);
        Assert.Equal("Other", warning.Reason);
        Assert.Equal("Pod", warning.ObjectKind);
        Assert.DoesNotContain("canary", JsonSerializer.Serialize(result));
        foreach (var key in Identity.ToDictionary().Keys)
            Assert.Contains(
                key,
                Uri.UnescapeDataString(
                    Assert.Single(handler.Requests, uri => uri.AbsolutePath.EndsWith("/pods", StringComparison.Ordinal))
                        .Query));
    }

    [Theory]
    [InlineData("foreign-uid", "app", "InstanceRemoved")]
    [InlineData("pod-uid", "exec-private-canary", "ContainerUnavailable")]
    [InlineData("pod-uid", "arbitrary-container", "ContainerUnavailable")]
    public async Task Unowned_instance_and_ineligible_container_never_open_a_log(
        string uid,
        string container,
        string expected)
    {
        using var handler = OwnedHandler();
        using var client = Client(handler);
        var result = await KubernetesWorkloadDiagnostics.OpenAsync(
            ct => Resolve(client, ct),
            new RuntimeLogSelection(uid, container, false, false, 100),
            CancellationToken.None);
        Assert.Equal(expected, Assert.IsType<UnavailableRuntimeLog>(result).Reason);
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath.EndsWith("/log", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deployment_discovery_requires_the_verified_replica_set_chain_and_reports_container_limits()
    {
        var own = Pod();
        own.Metadata.OwnerReferences[0].Uid = "owned-replica";
        own.Spec.Containers
            = Enumerable.Range(0, 33).Select(index => new V1Container { Name = $"app-{index}" }).ToList();
        var unrelated = Pod("unrelated");
        unrelated.Metadata.OwnerReferences[0].Uid = "foreign-replica";
        using var handler = new Handler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/application", StringComparison.Ordinal)
                ?
                KubernetesJson.Serialize(new V1Deployment { Metadata = Metadata("application", "deployment-uid") })
                : request.RequestUri.AbsolutePath.EndsWith("/replicasets", StringComparison.Ordinal)
                    ? KubernetesJson.Serialize(
                        new V1ReplicaSetList
                        {
                            Metadata = new V1ListMeta(),
                            Items =
                            [
                                new V1ReplicaSet { Metadata = Metadata("owned", "owned-replica", "deployment-uid") },
                                new V1ReplicaSet
                                    { Metadata = Metadata("foreign", "foreign-replica", "foreign-controller") },
                            ],
                        })
                    : request.RequestUri.AbsolutePath.EndsWith("/pods", StringComparison.Ordinal)
                        ? KubernetesJson.Serialize(
                            new V1PodList { Metadata = new V1ListMeta(), Items = [own, unrelated] })
                        : KubernetesJson.Serialize(new Corev1EventList { Metadata = new V1ListMeta(), Items = [] }));
        using var client = Client(handler);
        var workload = await KubernetesWorkloadDiagnostics.DeploymentAsync(
            client,
            "application",
            "owned",
            Identity,
            2,
            CancellationToken.None);
        var result = Assert.IsType<AvailableWorkloadDiagnostics>(
            await KubernetesWorkloadDiagnostics.DiscoverAsync(workload, CancellationToken.None));
        var instance = Assert.Single(result.Instances);
        Assert.Equal("pod-uid", instance.Uid);
        Assert.True(instance.ContainersTruncated);
        Assert.Equal(32, instance.Containers.Count(container => container.Kind == "Application"));
        Assert.DoesNotContain(handler.Requests, uri => uri.Query.Contains("foreign-replica", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Same_name_new_uid_after_open_discards_output_and_disposes_response_before_any_byte_is_read()
    {
        using var handler = OwnedHandler(replaced: true);
        using var client = Client(handler);
        var result = await KubernetesWorkloadDiagnostics.OpenAsync(
            ct => Resolve(client, ct),
            new RuntimeLogSelection("pod-uid", "app", false, true, 100),
            CancellationToken.None);
        Assert.Equal("InstanceReplaced", Assert.IsType<UnavailableRuntimeLog>(result).Reason);
        Assert.True(handler.LogContent!.Disposed);
        var uri = Assert.Single(handler.Requests, uri => uri.AbsolutePath.EndsWith("/log", StringComparison.Ordinal));
        Assert.Contains("container=app", uri.Query);
        Assert.Contains("follow=true", uri.Query);
        Assert.Contains("tailLines=100", uri.Query);
    }

    [Fact]
    public async Task Exact_owned_container_opens_output_and_periodic_verification_detects_replacement()
    {
        using var handler = OwnedHandler();
        using var client = Client(handler);
        var opened = Assert.IsType<OpenRuntimeLog>(
            await KubernetesWorkloadDiagnostics.OpenAsync(
                ct => Resolve(client, ct),
                new RuntimeLogSelection("pod-uid", "sidecar", true, false, 50),
                CancellationToken.None));
        await using (opened.Source)
        {
            using var reader = new StreamReader(opened.Source.Output, leaveOpen: true);
            Assert.Equal("controlled-runtime-marker\n", await reader.ReadToEndAsync());
            Assert.Null(await opened.Source.VerifyAsync(CancellationToken.None));
            handler.Replaced = true;
            Assert.Equal("InstanceReplaced", await opened.Source.VerifyAsync(CancellationToken.None));
        }

        Assert.True(handler.LogContent!.Disposed);
        var uri = Assert.Single(handler.Requests, uri => uri.AbsolutePath.EndsWith("/log", StringComparison.Ordinal));
        Assert.Contains("previous=true", uri.Query);
        Assert.Contains("limitBytes=1048576", uri.Query);
    }

    private static Handler OwnedHandler(bool replaced = false)
    {
        Handler? handler = null;
        handler = new Handler(request =>
                request.RequestUri!.AbsolutePath.EndsWith("/database", StringComparison.Ordinal)
                    ?
                    KubernetesJson.Serialize(new V1StatefulSet { Metadata = Metadata("database", "controller-uid") })
                    : request.RequestUri.AbsolutePath.EndsWith("/pods", StringComparison.Ordinal)
                        ? KubernetesJson.Serialize(new V1PodList { Metadata = new V1ListMeta(), Items = [Pod()] })
                        : KubernetesJson.Serialize(Pod(handler!.Replaced ? "replacement-uid" : "pod-uid")))
            { Replaced = replaced };
        return handler;
    }

    private static Kubernetes Client(Handler handler) => new(
        new KubernetesClientConfiguration { Host = "http://diagnostics.test" },
        handler);

    private sealed class TrackedContent(
        string output
    ) : StringContent(output, Encoding.UTF8, "text/plain")
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class Handler(
        Func<HttpRequestMessage, string> response
    ) : DelegatingHandler
    {
        public List<Uri> Requests { get; } = [];
        public bool Replaced { get; set; }
        public TrackedContent? LogContent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = request.RequestUri!.AbsolutePath.EndsWith("/log", StringComparison.Ordinal)
                        ? LogContent = new TrackedContent("controlled-runtime-marker\n")
                        : new StringContent(response(request), Encoding.UTF8, "application/json"),
                });
        }
    }
}
