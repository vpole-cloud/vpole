// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Pole.Workloads;

namespace Pole.Features.Core.Kubernetes;

/// <summary>Bounded reads of owned controllers/pods. Raw native messages and specifications never leave this boundary.</summary>
public static class KubernetesWorkloadObservation
{
    public const int MaximumPods = 100;
    public const string TemplateGenerationAnnotation = "pole.server/deploymentGeneration";

    private static readonly HashSet<string> SafeReasons = new(StringComparer.Ordinal)
    {
        "CrashLoopBackOff", "ImagePullBackOff", "ErrImagePull", "CreateContainerConfigError",
        "CreateContainerError", "RunContainerError", "OOMKilled", "Error", "Completed",
        "ContainerCreating", "PodInitializing", "FailedScheduling", "FailedMount",
        "FailedAttachVolume", "BackOff", "Unhealthy", "Failed", "FailedCreate", "FailedUpdate",
        "ProgressDeadlineExceeded", "ReplicaFailure", "Evicted", "Unschedulable", "ContainersNotReady",
        "FailedCreatePodSandBox", "FailedToRetrieveImagePullSecret", "InvalidImageName", "NodeLost", "DeadlineExceeded",
    };

    public static string SafeReason(string? reason) =>
        reason is not null && SafeReasons.Contains(reason) ? reason : "Other";

    public static bool Owns(V1ObjectMeta metadata, WorkloadIdentityLabels identity) =>
        !string.IsNullOrEmpty(metadata.Uid) && identity.ToDictionary()
            .All(pair => metadata.Labels?.TryGetValue(pair.Key, out var value) == true && value == pair.Value);

    public static long Generation(
        V1ObjectMeta metadata,
        string key,
        bool label = false) =>
        (label ? metadata.Labels : metadata.Annotations)?.TryGetValue(key, out var value) == true &&
        long.TryParse(value, out var generation)
            ? generation
            : 0;

    public static async Task<WorkloadObservation> DeploymentAsync(
        IKubernetes client,
        string name,
        string namespaceName,
        WorkloadIdentityLabels identity,
        long generation,
        CancellationToken cancellationToken)
    {
        var controller = await client.AppsV1.ReadNamespacedDeploymentAsync(
            name,
            namespaceName,
            cancellationToken: cancellationToken);
        if (!Owns(controller.Metadata, identity))
            return new UnavailableWorkloadObservation("OwnershipMismatch");
        var replicas = await client.AppsV1.ListNamespacedReplicaSetAsync(
            namespaceName,
            labelSelector: Selector(identity),
            limit: MaximumPods,
            cancellationToken: cancellationToken);
        var controllerUids = replicas.Items
            .Where(replica => replica.Metadata.NamespaceProperty == namespaceName && Owns(replica.Metadata, identity) &&
                ControlledBy(replica.Metadata, new HashSet<string>([controller.Metadata.Uid], StringComparer.Ordinal)))
            .Select(replica => replica.Metadata.Uid)
            .ToHashSet(StringComparer.Ordinal);
        var status = controller.Status;
        return await PodsAsync(
            client,
            namespaceName,
            identity,
            controller.Metadata,
            controllerUids,
            generation,
            TemplateGenerationAnnotation,
            false,
            Generation(controller.Spec.Template.Metadata, TemplateGenerationAnnotation) == generation &&
            status?.ObservedGeneration >= controller.Metadata.Generation &&
            string.IsNullOrEmpty(replicas.Metadata.ContinueProperty),
            controller.Spec.Replicas ?? 1,
            status?.Replicas ?? 0,
            status?.UpdatedReplicas ?? 0,
            status?.ReadyReplicas ?? 0,
            status?.AvailableReplicas ?? 0,
            cancellationToken);
    }

    /// <summary>Checks the source binding before reads; callers must revalidate again after diagnostics.</summary>
    public static async Task<bool> MatchesDeploymentBindingAsync(
        IKubernetes client,
        string name,
        string namespaceName,
        WorkloadIdentityLabels identity,
        string expectedUid,
        CancellationToken cancellationToken)
    {
        var controller = await client.AppsV1.ReadNamespacedDeploymentAsync(
            name,
            namespaceName,
            cancellationToken: cancellationToken);
        return controller.Metadata is { } metadata && metadata.Name == name &&
            metadata.NamespaceProperty == namespaceName && metadata.Uid == expectedUid && Owns(metadata, identity);
    }

    public static async Task<bool> MatchesStatefulSetBindingAsync(
        IKubernetes client,
        string name,
        string namespaceName,
        WorkloadIdentityLabels identity,
        string expectedUid,
        CancellationToken cancellationToken)
    {
        var controller = await client.AppsV1.ReadNamespacedStatefulSetAsync(
            name,
            namespaceName,
            cancellationToken: cancellationToken);
        return controller.Metadata is { } metadata && metadata.Name == name &&
            metadata.NamespaceProperty == namespaceName && metadata.Uid == expectedUid && Owns(metadata, identity);
    }

    public static async Task<WorkloadObservation> StatefulSetAsync(
        IKubernetes client,
        string name,
        string namespaceName,
        WorkloadIdentityLabels identity,
        long generation,
        string generationKey,
        bool generationLabel,
        CancellationToken cancellationToken)
    {
        var controller = await client.AppsV1.ReadNamespacedStatefulSetAsync(
            name,
            namespaceName,
            cancellationToken: cancellationToken);
        if (!Owns(controller.Metadata, identity))
            return new UnavailableWorkloadObservation("OwnershipMismatch");
        var status = controller.Status;
        return await PodsAsync(
            client,
            namespaceName,
            identity,
            controller.Metadata,
            new HashSet<string>([controller.Metadata.Uid], StringComparer.Ordinal),
            generation,
            generationKey,
            generationLabel,
            Generation(controller.Spec.Template.Metadata, generationKey, generationLabel) == generation &&
            status?.ObservedGeneration >= controller.Metadata.Generation &&
            !string.IsNullOrEmpty(status.UpdateRevision) && status.CurrentRevision == status.UpdateRevision,
            controller.Spec.Replicas ?? 1,
            status?.Replicas ?? 0,
            status?.UpdatedReplicas ?? 0,
            status?.ReadyReplicas ?? 0,
            status?.AvailableReplicas ?? 0,
            cancellationToken);
    }

    public static async Task<WorkloadObservation> PodsAsync(
        IKubernetes client,
        string namespaceName,
        WorkloadIdentityLabels identity,
        V1ObjectMeta controller,
        IReadOnlySet<string> controllerUids,
        long generation,
        string generationKey,
        bool generationLabel,
        bool controllerCurrent,
        int desired,
        int current,
        int updated,
        int ready,
        int available,
        CancellationToken cancellationToken)
    {
        if (!Owns(controller, identity))
            return new UnavailableWorkloadObservation("OwnershipMismatch");
        var listed = await client.CoreV1.ListNamespacedPodAsync(
            namespaceName,
            labelSelector: Selector(identity),
            limit: MaximumPods,
            cancellationToken: cancellationToken);
        var pods = listed.Items
            .Where(pod => pod.Metadata.NamespaceProperty == namespaceName && Owns(pod.Metadata, identity) &&
                ControlledBy(pod.Metadata, controllerUids))
            .OrderBy(pod => pod.Metadata.Name, StringComparer.Ordinal)
            .ToArray();
        var summary = pods.Select(pod => SummarizePod(pod, generation, generationKey, generationLabel)).ToArray();
        var truncated = !string.IsNullOrEmpty(listed.Metadata.ContinueProperty);
        var currentPods = pods.Where(pod => pod.Metadata.DeletionTimestamp is null).ToArray();
        var rolloutCurrent = controllerCurrent && !truncated && current == desired && updated == desired &&
            currentPods.Length == desired && currentPods.All(pod =>
                Generation(pod.Metadata, generationKey, generationLabel) == generation);
        var isReady = desired > 0 && rolloutCurrent && ready >= desired && available >= desired &&
            currentPods.All(pod => SummarizePod(pod, generation, generationKey, generationLabel).Ready);
        var events = new List<ObservedWorkloadEvent>();
        string? eventsUnavailable = null;
        try
        {
            // Eight verified UIDs, four events each: never scan or return another resource's namespace events.
            foreach (var uid in new[] { controller.Uid }.Concat(controllerUids.Order(StringComparer.Ordinal))
                         .Concat(pods.Select(pod => pod.Metadata.Uid).Order(StringComparer.Ordinal))
                         .Distinct(StringComparer.Ordinal)
                         .Take(8))
            {
                var recent = await client.CoreV1.ListNamespacedEventAsync(
                    namespaceName,
                    fieldSelector: $"involvedObject.uid={uid},type=Warning",
                    limit: 4,
                    cancellationToken: cancellationToken);
                events.AddRange(
                    recent.Items
                        .Where(item => item.InvolvedObject.Uid == uid && item.Type == "Warning")
                        .Select(item => new ObservedWorkloadEvent(
                            SafeReason(item.Reason),
                            item.Count ?? 1,
                            item.LastTimestamp ?? item.EventTime)));
            }
        }
        catch (HttpOperationException exception) when (exception.Response?.StatusCode is HttpStatusCode.Forbidden
                                                           or HttpStatusCode.Unauthorized)
        {
            eventsUnavailable = "Forbidden";
        }
        catch (HttpRequestException)
        {
            eventsUnavailable = "Unavailable";
        }
        catch (HttpOperationException)
        {
            eventsUnavailable = "Unavailable";
        }

        return new ObservedWorkload(
            isReady,
            rolloutCurrent,
            desired,
            current,
            updated,
            ready,
            available,
            truncated,
            summary,
            events.OrderByDescending(item => item.LastSeenAtUtc).Take(32).ToArray(),
            eventsUnavailable);
    }

    public static ObservedWorkloadPod SummarizePod(
        V1Pod pod,
        long generation,
        string generationKey,
        bool generationLabel)
    {
        var containers = (pod.Status?.ContainerStatuses ?? []).Concat(pod.Status?.InitContainerStatuses ?? [])
            .ToArray();
        var ready = pod.Metadata.DeletionTimestamp is null && pod.Status?.Phase == "Running" &&
            pod.Status.Conditions?.Any(condition => condition.Type == "Ready" && condition.Status == "True") == true &&
            pod.Status.ContainerStatuses?.Count > 0 && pod.Status.ContainerStatuses.All(container => container.Ready);
        var reasons = containers
            .Select(container => container.State?.Waiting?.Reason ??
                container.State?.Terminated?.Reason ?? container.LastState?.Terminated?.Reason)
            .Concat(
                pod.Status
                    ?.Conditions
                    ?.Where(condition => condition.Status == "False")
                    .Select(condition => condition.Reason) ?? [])
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Select(SafeReason)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();
        var phase = pod.Metadata.DeletionTimestamp is not null
            ? "Terminating"
            : pod.Status?.Phase switch
            {
                "Pending" => "Pending",
                "Running" => "Running",
                "Succeeded" => "Succeeded",
                "Failed" => "Failed",
                _ => "Unknown",
            };
        return new ObservedWorkloadPod(
            pod.Metadata.Name,
            phase,
            ready,
            Generation(pod.Metadata, generationKey, generationLabel) == generation,
            containers.Sum(container => container.RestartCount),
            reasons);
    }

    private static string Selector(WorkloadIdentityLabels identity) => string.Join(
        ',',
        identity.ToDictionary().Select(pair => $"{pair.Key}={pair.Value}"));

    private static bool ControlledBy(V1ObjectMeta metadata, IReadOnlySet<string> uids) =>
        metadata.OwnerReferences?.Any(owner => owner.Controller == true && uids.Contains(owner.Uid)) == true;
}
