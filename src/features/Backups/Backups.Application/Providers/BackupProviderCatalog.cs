// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.Backups.Contracts;
using Pole.Workloads;

namespace Pole.Features.Backups.Application.Providers;

public sealed class BackupProviderCatalog
{
    private readonly Dictionary<string, IBackupProvider> _providers;

    public BackupProviderCatalog(IEnumerable<IBackupProvider> providers)
    {
        _providers = providers.ToDictionary(p => p.ResourceType, StringComparer.Ordinal);
        var kinds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in _providers.Values)
            if (!ResourceTypeId.TryCreate(provider.ResourceType, out _) ||
                string.IsNullOrWhiteSpace(provider.DeclarationKind) || !kinds.Add(provider.DeclarationKind))
                throw new InvalidOperationException(
                    "Backup providers require unique resource types and declaration kinds.");
    }

    public IBackupProvider? Find(string resourceType) => _providers.GetValueOrDefault(resourceType);
}
