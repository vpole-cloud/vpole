// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Secrets.Primitives;
using Pole.Workloads;

namespace Pole.Features.Secrets.Contracts;

public enum SecretConsumerProjectionPolicy
{
    RuntimeBindings,
    DeclaredPurposeForAllDeployments,
}

public enum SecretConsumerPlacementRequirement
{
    LiveDeployments,
    AllowBeforePlacement,
}

public enum SecretConsumerValueAccess
{
    ProjectionOnly,
    GrantedKeys,
}

/// <summary>The resource owner contributes its supported declaration binding capability.</summary>
public sealed record SecretDeclarationConsumer(
    string Kind,
    ResourceTypeId ResourceType,
    SecretBindingUsage Usage,
    SecretConsumerProjectionPolicy Projection,
    SecretConsumerPlacementRequirement Placement,
    SecretConsumerValueAccess ValueAccess
);
