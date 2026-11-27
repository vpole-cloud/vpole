// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.FeatureName.Domain;

namespace Pole.Features.FeatureName.Application.Common;

public sealed record ResourceNameDto(
    string Id,
    ResourceNameDetailsDto Details,
    long Revision
)
{
    public static ResourceNameDto From(ResourceName resource) => new(
        resource.Id,
        new(resource.Details.Name, resource.Details.Description),
        resource.Revision);
}

public sealed record ResourceNameNotFound(
    string Id
);

public sealed record ResourceNameNameAlreadyExists(
    string Name
);

public sealed record ResourceNameRevisionConflict(
    string Id,
    long ExpectedRevision
);
