// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Secrets.Application.Declarations;

public enum SecretBindingDeclarationError
{
    BindingPrerequisiteUnavailable,
    SecretUnavailable,
    ImmutableBindingChange,
    ExplicitAdoptionRequired,
    SecretKeyUnavailable,
    BindingScopeUnavailable,
    SecretBindingLimitReached,
    SecretKeysInUse,
}

public sealed record SecretBindingDeclarationRejected(
    SecretBindingDeclarationError Reason
) : IDeclarationFailure;
