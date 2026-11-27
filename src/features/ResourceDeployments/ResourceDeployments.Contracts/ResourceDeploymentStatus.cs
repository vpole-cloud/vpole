// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json.Serialization;

namespace Pole.Features.ResourceDeployments.Contracts;

[JsonConverter(typeof(ResourceDeploymentStatusJsonConverter))]
public enum ResourceDeploymentStatus
{
    Prepared,
    WaitingForRuntimeCell,
    Pending,
    Deleting,
    Reconciling,
    Applied,
    DeleteFailed,
    Failed,
    Deleted,
}

[JsonConverter(typeof(ResourceDeploymentDesiredOperationJsonConverter))]
public enum ResourceDeploymentDesiredOperation { Apply, Delete }

// These are HTTP boundary converters; in-process contract consumers retain typed values.
public sealed class ResourceDeploymentStatusJsonConverter()
    : JsonStringEnumConverter<ResourceDeploymentStatus>(allowIntegerValues: false);

public sealed class ResourceDeploymentDesiredOperationJsonConverter()
    : JsonStringEnumConverter<ResourceDeploymentDesiredOperation>(allowIntegerValues: false);
