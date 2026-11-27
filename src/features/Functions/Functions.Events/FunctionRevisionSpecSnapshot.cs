// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Pole.Features.Functions.Events;

public sealed record FunctionRevisionSpecSnapshot(
    FunctionImageSnapshot Image,
    string RuntimeProtocolVersion,
    FunctionConfigurationSnapshot Configuration,
    FunctionResourcesSnapshot Resources,
    FunctionScalingPolicySnapshot Scaling,
    ImmutableList<FunctionHandlerSnapshot> Handlers
);

public sealed record FunctionImageSnapshot(
    string Server,
    string Name,
    string Tag,
    string? Digest,
    string? PullSecretName
);

public sealed record FunctionConfigurationSnapshot(
    ImmutableDictionary<string, string> EnvironmentVariables,
    ImmutableDictionary<string, FunctionSecretReferenceSnapshot> SecretReferences
);

public sealed record FunctionSecretReferenceSnapshot(
    string SecretId,
    string Key
);

public sealed record FunctionResourcesSnapshot(
    FunctionResourceValuesSnapshot Requests,
    FunctionResourceValuesSnapshot Limits
);

public sealed record FunctionResourceValuesSnapshot(
    int CpuMillis,
    int MemoryMi
);

public sealed record FunctionScalingPolicySnapshot(
    int MinInstances,
    int MaxInstances,
    int TargetConcurrencyPerInstance,
    int MaxConcurrencyPerInstance,
    int ScaleDownDelaySeconds,
    int ActivationTimeoutSeconds
);

public sealed record FunctionHandlerSnapshot(
    string Name,
    int MaxConcurrencyPerInstance,
    FunctionTriggerSnapshot Trigger
);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HttpFunctionTriggerSnapshot), "http")]
[JsonDerivedType(typeof(TimerFunctionTriggerSnapshot), "timer")]
public abstract record FunctionTriggerSnapshot(
    string TriggerId
);

public sealed record HttpFunctionTriggerSnapshot(
    string TriggerId,
    string Route,
    ImmutableHashSet<string> Methods,
    string Exposure
) : FunctionTriggerSnapshot(TriggerId);

public sealed record TimerFunctionTriggerSnapshot(
    string TriggerId,
    string Cron,
    string TimeZone,
    string ConcurrencyPolicy,
    string MisfirePolicy,
    int ScheduleVersion,
    FunctionRetryPolicySnapshot Retry
) : FunctionTriggerSnapshot(TriggerId);

public sealed record FunctionRetryPolicySnapshot(
    int MaxAttempts,
    ImmutableList<int> BackoffSeconds
);
