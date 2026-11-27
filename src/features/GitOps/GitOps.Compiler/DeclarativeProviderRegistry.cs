// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.GitOps.Contracts;

namespace Pole.Features.GitOps.Compiler;

public sealed class DeclarativeProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IDeclarativeResourceProvider> providers;

    public DeclarativeProviderRegistry(IEnumerable<IDeclarativeResourceProvider> contributions)
    {
        providers = contributions.ToDictionary(provider => provider.Kind, StringComparer.Ordinal);
    }

    public IDeclarativeResourceProvider? Find(string kind) => providers.GetValueOrDefault(kind);
}
