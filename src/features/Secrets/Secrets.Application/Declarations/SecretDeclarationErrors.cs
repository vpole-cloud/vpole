// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Secrets.Application.Declarations;

public enum SecretDeclarationError
{
    ResourceGroupUnavailable,
    ExplicitAdoptionRequired,
    ImmutablePlacementChange,
    ResourceScopeUnavailable,
    ResourceRegistrationUnavailable,
    SecretKeysInUse,
    ProviderManagedSecret,
}

public sealed record SecretDeclarationRejected(
    SecretDeclarationError Reason
) : IDeclarationFailure;
