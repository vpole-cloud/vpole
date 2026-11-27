// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitOps.Contracts;

namespace Pole.Features.ResourceGroups.Application.ResourceGroups;

public enum ResourceGroupDeclarationError
{
    NameTooLong,
    ExplicitAdoptionRequired,
    ResourceDeleting,
    ExpectedRevisionConflict,
}

public sealed record ResourceGroupDeclarationRejected(
    ResourceGroupDeclarationError Reason
) : IDeclarationFailure;

public sealed record ResourceGroupDeclarationApplied(
    string ResourceId,
    bool Changed
);

public union ApplyResourceGroupDeclarationResult(ResourceGroupDeclarationApplied, ResourceGroupDeclarationRejected);
