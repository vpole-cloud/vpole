// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitOps.Contracts;

namespace Pole.Features.ResourceGroups.Application.Common;

public sealed record ResourceGroupDto(
    string Id,
    string Name,
    string Description,
    string DefaultIsolationScopeId,
    string Lifecycle,
    long Revision,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    long ResourceCount,
    IEnumerable<string> Tags,
    GitResourceStatus? GitOps = null
);

public sealed record ResourceRegistrationDto(
    string RegistrationId,
    string ResourceId,
    string ResourceTypeId,
    string DisplayName,
    string? OwningPluginId,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
);
