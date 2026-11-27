// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitOps.Contracts;

namespace Pole.Features.GitOps.Application;

public sealed class RepositoryProviderRegistry(
    IEnumerable<IRepositorySnapshotProvider> providers
)
{
    private readonly IReadOnlyDictionary<string, IRepositorySnapshotProvider> _providers
        = providers.ToDictionary(provider => provider.Provider, StringComparer.Ordinal);

    public IRepositorySnapshotProvider? Find(string provider) => _providers.GetValueOrDefault(provider);
}
