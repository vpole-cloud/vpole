// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Events.Abstractions;
using Pole.Features.GitProviders.Domain;
using Pole.Features.GitProviders.Events;

namespace Pole.Features.GitProviders.Application.Common;

internal static class GitMetadataEventExtensions
{
    public static GitProviderConnectionChangedEvent ToChangedEvent(this GitProviderConnection connection) =>
        new(connection.Id, EventCore.Create(connection.TenantId));

    public static SourceRepositoryChangedEvent ToChangedEvent(this SourceRepository repository) => new(
        repository.Id,
        repository.ConnectionId,
        repository.TechnicalName,
        repository.Revision,
        EventCore.Create(repository.TenantId));
}
