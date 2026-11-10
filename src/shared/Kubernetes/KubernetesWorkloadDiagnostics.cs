using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Workloads;

namespace Pole.Features.Core.Kubernetes;

/// <summary>Provider-chosen controller and authoritative placement. Native specifications stay here.</summary>
public sealed class OwnedKubernetesWorkload(
    IKubernetes client,
    string namespaceName,
    WorkloadIdentityLabels identity,
    V1ObjectMeta controller,
    string controllerKind,
    IReadOnlyList<V1ObjectMeta> replicaSets,
    IReadOnlySet<string> podOwnerUids,
    bool controllersTruncated,
    long generation,
    string generationKey,
    bool generationLabel
)
{
    public IKubernetes Client { get; } = client;

    public string Namespace { get; } = namespaceName;

    public WorkloadIdentityLabels Identity { get; } = identity;

    public V1ObjectMeta Controller { get; } = controller;

    public string ControllerKind { get; } = controllerKind;

    public IReadOnlyList<V1ObjectMeta> ReplicaSets { get; } = replicaSets;

    public IReadOnlySet<string> PodOwnerUids { get; } = podOwnerUids;

    public bool ControllersTruncated { get; } = controllersTruncated;

    public long Generation { get; } = generation;

    public string GenerationKey { get; } = generationKey;

    public bool GenerationLabel { get; } = generationLabel;
}

public static class KubernetesWorkloadDiagnostics
{
    private static string Selector(WorkloadIdentityLabels identity) => string.Join(
        ',',
        identity.ToDictionary().Select(pair => $"{pair.Key}={pair.Value}"));

    private static bool ControlledBy(V1ObjectMeta metadata, IReadOnlySet<string> owners) =>
        metadata.OwnerReferences?.Any(owner => owner.Controller == true && owners.Contains(owner.Uid)) == true;

    public static async Task<OwnedKubernetesWorkload?> DeploymentAsync(
        IKubernetes client,
        string name,
        string namespaceName,
        WorkloadIdentityLabels identity,
        long generation,
        CancellationToken ct)
    {
        var controller = await client.AppsV1.ReadNamespacedDeploymentAsync(name, namespaceName, cancellationToken: ct);
        if (!KubernetesWorkloadObservation.Owns(controller.Metadata, identity))
            return null;
        var replicas = await client.AppsV1.ListNamespacedReplicaSetAsync(
            namespaceName,
            labelSelector: Selector(identity),
            limit: 100,
            cancellationToken: ct);
        var owned = replicas.Items
            .Where(replica => replica.Metadata.NamespaceProperty == namespaceName &&
                KubernetesWorkloadObservation.Owns(replica.Metadata, identity) && ControlledBy(
                    replica.Metadata,
                    new HashSet<string>([controller.Metadata.Uid], StringComparer.Ordinal)))
            .Select(replica => replica.Metadata)
            .ToArray();
        return new OwnedKubernetesWorkload(
            client,
            namespaceName,
            identity,
            controller.Metadata,
            "Deployment",
            owned,
            owned.Select(replica => replica.Uid).ToHashSet(StringComparer.Ordinal),
            !string.IsNullOrEmpty(replicas.Metadata.ContinueProperty),
            generation,
            KubernetesWorkloadObservation.TemplateGenerationAnnotation,
            false);
    }

    public static async Task<OwnedKubernetesWorkload?> StatefulSetAsync(
        IKubernetes client,
        string name,
        string namespaceName,
        WorkloadIdentityLabels identity,
        long generation,
        string generationKey,
        bool generationLabel,
        CancellationToken ct)
    {
        var controller = await client.AppsV1.ReadNamespacedStatefulSetAsync(name, namespaceName, cancellationToken: ct);
        return Controller(
            client,
            namespaceName,
            identity,
            controller.Metadata,
            "StatefulSet",
            generation,
            generationKey,
            generationLabel);
    }

    public static OwnedKubernetesWorkload? Controller(
        IKubernetes client,
        string namespaceName,
        WorkloadIdentityLabels identity,
        V1ObjectMeta controller,
        string kind,
        long generation,
        string generationKey,
        bool generationLabel) => KubernetesWorkloadObservation.Owns(controller, identity)
        ? new OwnedKubernetesWorkload(
            client,
            namespaceName,
            identity,
            controller,
            kind,
            [],
            new HashSet<string>([controller.Uid], StringComparer.Ordinal),
            false,
            generation,
            generationKey,
            generationLabel)
        : null;

    private static async Task<(V1Pod[] Pods, bool Truncated)> PodsAsync(
        OwnedKubernetesWorkload workload,
        CancellationToken ct)
    {
        var list = await workload.Client.CoreV1.ListNamespacedPodAsync(
            workload.Namespace,
            labelSelector: Selector(workload.Identity),
            limit: 100,
            cancellationToken: ct);
        return (
            list.Items
                .Where(pod =>
                    pod.Metadata.NamespaceProperty == workload.Namespace &&
                    KubernetesWorkloadObservation.Owns(pod.Metadata, workload.Identity) &&
                    ControlledBy(pod.Metadata, workload.PodOwnerUids))
                .OrderBy(pod => pod.Metadata.Name, StringComparer.Ordinal)
                .Take(100)
                .ToArray(), workload.ControllersTruncated || !string.IsNullOrEmpty(list.Metadata.ContinueProperty));
    }

    private static DiagnosticWorkloadContainer[] Containers(V1Pod pod) => (pod.Spec?.Containers ?? []).Take(32)
        .Select(container => new DiagnosticWorkloadContainer(
            container.Name,
            "Application",
            pod.Status?.ContainerStatuses?.Any(status =>
                status.Name == container.Name && status.RestartCount > 0 && status.LastState?.Terminated is not null) ==
            true))
        .Concat(
            (pod.Spec?.InitContainers ?? []).Take(32)
            .Select(container => new DiagnosticWorkloadContainer(
                container.Name,
                "Init",
                pod.Status?.InitContainerStatuses?.Any(status =>
                    status.Name == container.Name && status.RestartCount > 0 &&
                    status.LastState?.Terminated is not null) == true)))
        .ToArray(); // Ephemeral/exec containers are never eligible.

    public static async Task<WorkloadDiagnostics> DiscoverAsync(OwnedKubernetesWorkload? workload, CancellationToken ct)
    {
        if (workload is null)
            return new UnavailableWorkloadDiagnostics("OwnershipNotVerified");
        var (pods, truncated) = await PodsAsync(workload, ct);
        var instances = pods.Select(pod =>
            {
                var summary = KubernetesWorkloadObservation.SummarizePod(
                    pod,
                    workload.Generation,
                    workload.GenerationKey,
                    workload.GenerationLabel);
                return new DiagnosticWorkloadInstance(
                    pod.Metadata.Uid,
                    summary.Name,
                    summary.Phase,
                    summary.Ready,
                    summary.CurrentGeneration,
                    summary.RestartCount,
                    summary.Reasons,
                    Containers(pod),
                    (pod.Spec?.Containers?.Count ?? 0) > 32 || (pod.Spec?.InitContainers?.Count ?? 0) > 32);
            })
            .ToArray();
        var objects = new[] { (Kind: workload.ControllerKind, Metadata: workload.Controller) }
            .Concat(
                workload.ReplicaSets
                    .OrderBy(metadata => metadata.Uid, StringComparer.Ordinal)
                    .Select(metadata => (Kind: "ReplicaSet", Metadata: metadata)))
            .Concat(
                pods.OrderBy(pod => pod.Metadata.Uid, StringComparer.Ordinal)
                    .Select(pod => (Kind: "Pod", pod.Metadata)))
            .DistinctBy(item => item.Metadata.Uid)
            .ToArray();
        var events = new List<DiagnosticWorkloadEvent>();
        var eventsTruncated = objects.Length > 8 || truncated;
        string? unavailable = null;
        try
        {
            foreach (var item in objects.Take(8))
            {
                var list = await workload.Client.CoreV1.ListNamespacedEventAsync(
                    workload.Namespace,
                    fieldSelector: $"involvedObject.uid={item.Metadata.Uid},type=Warning",
                    limit: 4,
                    cancellationToken: ct);
                eventsTruncated |= !string.IsNullOrEmpty(list.Metadata.ContinueProperty);
                events.AddRange(
                    list.Items
                        .Where(entry => entry.InvolvedObject?.Uid == item.Metadata.Uid && entry.Type == "Warning")
                        .Take(4)
                        .Select(entry => new DiagnosticWorkloadEvent(
                            item.Kind,
                            item.Metadata.Name,
                            KubernetesWorkloadObservation.SafeReason(entry.Reason),
                            entry.Count ?? 1,
                            entry.LastTimestamp ?? entry.EventTime)));
            }
        }
        catch (HttpOperationException exception)
        {
            unavailable = exception.Response?.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized
                ? "Forbidden"
                : "TargetUnavailable";
        }
        catch (HttpRequestException)
        {
            unavailable = "TargetUnavailable";
        }

        return new AvailableWorkloadDiagnostics(
            instances,
            truncated,
            events.OrderByDescending(entry => entry.LastSeenAtUtc).Take(32).ToArray(),
            eventsTruncated,
            unavailable);
    }

    public static async Task<RuntimeLogOpenResult> OpenAsync(
        Func<CancellationToken, Task<OwnedKubernetesWorkload?>> resolve,
        RuntimeLogSelection selection,
        CancellationToken ct)
    {
        var workload = await resolve(ct);
        if (workload is null)
            return new UnavailableRuntimeLog("OwnershipNotVerified");
        var (pods, _) = await PodsAsync(workload, ct);
        var pod = pods.SingleOrDefault(pod => pod.Metadata.Uid == selection.InstanceUid);
        if (pod is null || pod.Metadata.DeletionTimestamp is not null)
            return new UnavailableRuntimeLog("InstanceRemoved");
        if (!Containers(pod).Any(container => container.Name == selection.Container))
            return new UnavailableRuntimeLog("ContainerUnavailable");
        var response = await workload.Client.CoreV1.ReadNamespacedPodLogWithHttpMessagesAsync(
            pod.Metadata.Name,
            workload.Namespace,
            container: selection.Container,
            follow: selection.Follow,
            previous: selection.Previous,
            timestamps: true,
            tailLines: selection.TailLines,
            limitBytes: selection.Follow ? null : 1024 * 1024,
            cancellationToken: ct);
        var source = new RuntimeLogSource(
            response.Body,
            async token =>
            {
                var current = await resolve(token);
                if (current is null || current.Controller.Uid != workload.Controller.Uid)
                    return "OwnershipChanged";
                var selected = await current.Client.CoreV1.ReadNamespacedPodAsync(
                    pod.Metadata.Name,
                    current.Namespace,
                    cancellationToken: token);
                if (selected.Metadata.Uid != selection.InstanceUid || selected.Metadata.DeletionTimestamp is not null)
                    return "InstanceReplaced";
                if (!KubernetesWorkloadObservation.Owns(selected.Metadata, current.Identity) ||
                    !ControlledBy(selected.Metadata, current.PodOwnerUids))
                    return "OwnershipChanged";
                return Containers(selected).Any(container => container.Name == selection.Container)
                    ? null
                    : "ContainerUnavailable";
            },
            response.Dispose);
        try
        {
            var changed = await source.VerifyAsync(ct);
            if (changed is null)
                return new OpenRuntimeLog(source);
            await source.DisposeAsync();
            return new UnavailableRuntimeLog(changed);
        }
        catch
        {
            await source.DisposeAsync();
            throw;
        }
    }
}
