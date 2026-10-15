using System.Security.Cryptography;
using System.Text.Json;
using Pole.Features.Core.Kubernetes;
using Pole.Features.ResourceDeployments.Application.Observation;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Application.Persistence.Observation;

internal static class ObservationValueMappings
{
    private static readonly HashSet<string> UnavailableReasons = new(StringComparer.Ordinal)
    {
        "Unsupported", "Timeout", "TargetUnavailable", "NotFound", "Forbidden", "ClusterUnavailable",
        "Superseded", "OwnershipMismatch", "OwnershipNotVerified", "Unavailable", "BindingChanged"
    };

    internal static ObservationContextPin Pin(ResourceDeploymentObservationContext context) => new(
        context.Deployment.ResourceType.Value,
        context.Deployment.ResourceId,
        context.Deployment.ResourceGroupId,
        context.Deployment.IsolationScopeId,
        context.Deployment.RuntimeCellId,
        context.Deployment.ResourceRevision,
        context.DeploymentRevision,
        context.Deployment.DesiredGeneration,
        context.Deployment.Placement.DeploymentTargetId,
        context.TargetConnectionVersion,
        context.Deployment.Placement.TargetWorkloadConfigurationRevision,
        context.Deployment.Placement.KubernetesNamespace);

    internal static StoredObservation Store(WorkloadObservation value) => value switch
    {
        UnavailableWorkloadObservation unavailable => new("Unavailable", SafeUnavailable(unavailable.Reason), null),
        ObservedWorkload observed => new("Observed",
            null,
            new(
                observed.Ready && !observed.PodsTruncated && observed.Pods.Count <= 100,
                observed.RolloutCurrent && !observed.PodsTruncated && observed.Pods.Count <= 100,
                observed.DesiredReplicas,
                observed.CurrentReplicas,
                observed.UpdatedReplicas,
                observed.ReadyReplicas,
                observed.AvailableReplicas,
                observed.PodsTruncated || observed.Pods.Count > 100,
                observed.Pods.Take(100).Select(pod => new StoredPod(Name(pod.Name),
                    Phase(pod.Phase),
                    pod.Ready,
                    pod.CurrentGeneration,
                    pod.RestartCount,
                    Reasons(pod.Reasons))).OrderBy(pod => pod.Name, StringComparer.Ordinal).ToArray(),
                observed.Events.Take(32).Select(item => new StoredEvent(
                    KubernetesWorkloadObservation.SafeReason(item.Reason),
                    item.Count,
                    Utc(item.LastSeenAtUtc))).ToArray(),
                SafeOptionalUnavailable(observed.EventsUnavailableReason))),
        _ => throw new ArgumentException("Unsupported observation value.", nameof(value))
    };

    internal static StoredDiagnostics? Store(WorkloadDiagnostics? value) => value switch
    {
        null => null,
        UnavailableWorkloadDiagnostics unavailable => new("Unavailable", SafeUnavailable(unavailable.Reason), null),
        AvailableWorkloadDiagnostics available => new("Available",
            null,
            new(
                available.Instances.Take(100).Select(instance => new StoredInstance(Identity(instance.Uid),
                        Name(instance.Name),
                        Phase(instance.Phase),
                        instance.Ready,
                        instance.CurrentGeneration,
                        instance.RestartCount,
                        Reasons(instance.Reasons),
                        instance.Containers.Where(container => container.Kind is "Application" or "Init").Take(64)
                            .Select(container =>
                                new StoredContainer(Name(container.Name), container.Kind, container.PreviousAvailable))
                            .ToArray(),
                        instance.ContainersTruncated || instance.Containers.Count > 64))
                    .OrderBy(instance => instance.Uid, StringComparer.Ordinal).ToArray(),
                available.InstancesTruncated || available.Instances.Count > 100,
                available.Events.Take(32).Select(item => new StoredDiagnosticEvent(ObjectKind(item.ObjectKind),
                    Name(item.ObjectName),
                    KubernetesWorkloadObservation.SafeReason(item.Reason),
                    item.Count,
                    Utc(item.LastSeenAtUtc))).ToArray(),
                available.EventsTruncated || available.Events.Count > 32,
                SafeOptionalUnavailable(available.EventsUnavailableReason)))
    };

    internal static WorkloadObservation Load(StoredObservation value) => value switch
    {
        { Kind: "Unavailable", Reason: { } reason, Observed: null } => new UnavailableWorkloadObservation(reason),
        { Kind: "Observed", Reason: null, Observed: { } observed } => new ObservedWorkload(observed.Ready,
            observed.RolloutCurrent,
            observed.DesiredReplicas,
            observed.CurrentReplicas,
            observed.UpdatedReplicas,
            observed.ReadyReplicas,
            observed.AvailableReplicas,
            observed.PodsTruncated,
            observed.Pods.Select(pod => new ObservedWorkloadPod(pod.Name,
                pod.Phase,
                pod.Ready,
                pod.CurrentGeneration,
                pod.RestartCount,
                pod.Reasons)).ToArray(),
            observed.Events.Select(item => new ObservedWorkloadEvent(item.Reason, item.Count, item.LastSeenAtUtc))
                .ToArray(),
            observed.EventsUnavailableReason),
        _ => throw new InvalidOperationException("Invalid stored observation kind.")
    };

    internal static WorkloadDiagnostics? Load(StoredDiagnostics? value) => value switch
    {
        null => null,
        { Kind: "Unavailable", Reason: { } reason, Available: null } => new UnavailableWorkloadDiagnostics(reason),
        { Kind: "Available", Reason: null, Available: { } available } => new AvailableWorkloadDiagnostics(
            available.Instances.Select(instance => new DiagnosticWorkloadInstance(instance.Uid,
                instance.Name,
                instance.Phase,
                instance.Ready,
                instance.CurrentGeneration,
                instance.RestartCount,
                instance.Reasons,
                instance.Containers.Select(container =>
                        new DiagnosticWorkloadContainer(container.Name, container.Kind, container.PreviousAvailable))
                    .ToArray(),
                instance.ContainersTruncated)).ToArray(),
            available.InstancesTruncated,
            available.Events.Select(item =>
                new DiagnosticWorkloadEvent(item.ObjectKind,
                    item.ObjectName,
                    item.Reason,
                    item.Count,
                    item.LastSeenAtUtc)).ToArray(),
            available.EventsTruncated,
            available.EventsUnavailableReason),
        _ => throw new InvalidOperationException("Invalid stored diagnostics kind.")
    };

    internal static ResourceDeploymentSharedObservation Load(ObservationSnapshotDocument snapshot,
        ResourceDeploymentObservationContext context) => new(
        context,
        snapshot.BindingId,
        snapshot.SourceControlVersion,
        snapshot.ObservationVersion,
        snapshot.CheckedAtUtc,
        Load(snapshot.Observation),
        Load(snapshot.Diagnostics));

    internal static string Fingerprint(StoredObservation observation, StoredDiagnostics? diagnostics) =>
        Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                Observation = observation,
                Diagnostics = diagnostics
            })));

    private static string[] Reasons(IReadOnlyList<string> reasons) => reasons
        .Select(KubernetesWorkloadObservation.SafeReason)
        .Distinct(StringComparer.Ordinal).Take(8).ToArray();

    private static string SafeUnavailable(string reason) =>
        UnavailableReasons.Contains(reason) ? reason : "Unavailable";

    private static string? SafeOptionalUnavailable(string? reason) => reason is null ? null : SafeUnavailable(reason);

    private static string Phase(string phase) =>
        phase is "Pending" or "Running" or "Succeeded" or "Failed" or "Terminating" ? phase : "Unknown";

    private static string ObjectKind(string kind) =>
        kind is "Deployment" or "StatefulSet" or "ReplicaSet" or "Pod" or "Service" or "Ingress" ? kind : "Other";

    private static DateTime? Utc(DateTime? value) => value is null
        ? null
        : value.Value.Kind == DateTimeKind.Local
            ? value.Value.ToUniversalTime()
            : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

    private static string Name(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 253 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '.' and not '_'))
            throw new ArgumentException("Invalid workload metadata name.");
        return value;
    }

    private static string Identity(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 512 || value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '.' and not '_' and not ':'))
            throw new ArgumentException("Invalid workload metadata identity.");
        return value;
    }
}
