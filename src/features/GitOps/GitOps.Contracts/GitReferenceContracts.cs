// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

namespace Pole.Features.GitOps.Contracts;

public sealed record ResolveAcceptedGitReferenceRequest(
    GitMutationContext Context,
    string ConsumerResourceId,
    ResourceAddress Address
);

public sealed record GitReferenceTarget(
    ResourceAddress Address,
    string ResourceId
);

public sealed record PinGitReferenceRequest(
    GitMutationContext Context,
    string ConsumerResourceId,
    GitReferenceTarget Target
);

public sealed record GitReferencePinned(
    string ResourceId
);

public union PinGitReferenceOutcome(GitReferencePinned, StaleGitExecution);
