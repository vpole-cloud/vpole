// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitProviders.Contracts;
using Pole.Features.GitProviders.Domain;

namespace Pole.Features.GitProviders.Application.Common;

internal static class SourceRepositoryBuildMappings
{
    public static SourceRepositoryBuildSnapshot ToBuildSnapshot(this SourceRepository repository) => new(
        repository.Id,
        repository.TechnicalName,
        repository.ConnectionId,
        repository.ExternalRepositoryId,
        repository.Branch,
        repository.Revision);
}
