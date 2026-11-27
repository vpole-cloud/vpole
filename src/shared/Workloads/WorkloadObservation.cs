// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json.Serialization;

namespace Pole.Workloads;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ObservedWorkload), "Observed")]
[JsonDerivedType(typeof(UnavailableWorkloadObservation), "Unavailable")]
public closed record WorkloadObservation;

public sealed record UnavailableWorkloadObservation(
    string Reason
) : WorkloadObservation;

public sealed record ObservedWorkload(
    bool Ready,
    bool RolloutCurrent,
    int DesiredReplicas,
    int CurrentReplicas,
    int UpdatedReplicas,
    int ReadyReplicas,
    int AvailableReplicas,
    bool PodsTruncated,
    IReadOnlyList<ObservedWorkloadPod> Pods,
    IReadOnlyList<ObservedWorkloadEvent> Events,
    string? EventsUnavailableReason
) : WorkloadObservation;

public sealed record ObservedWorkloadPod(
    string Name,
    string Phase,
    bool Ready,
    bool CurrentGeneration,
    int RestartCount,
    IReadOnlyList<string> Reasons
);

public sealed record ObservedWorkloadEvent(
    string Reason,
    int Count,
    DateTime? LastSeenAtUtc
);
