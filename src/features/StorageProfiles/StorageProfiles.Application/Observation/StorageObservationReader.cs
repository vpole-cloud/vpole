// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Application.Discovery.Observation;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application.Observation;

public union ReadStorageObservationOutcome(StorageObservationSnapshot, KubernetesStorageDiscoveryUnavailable);

public sealed class StorageObservationReader(
    IStorageObservationStore store,
    StorageObservationContextLoader contexts,
    StorageObservationNativeReader native,
    StorageObservationOptions options,
    TimeProvider clock,
    StorageObservationMetrics? metrics = null
)
{
    internal async Task<ReadStorageObservationOutcome> ReadLegacyAsync(
        string targetId,
        StorageInventoryKind kind,
        CancellationToken ct)
    {
        var readKind = kind switch
        {
            StorageInventoryKind.Classes => StorageObservationReadKind.Classes,
            StorageInventoryKind.Volumes => StorageObservationReadKind.Volumes,
            StorageInventoryKind.Claims => StorageObservationReadKind.Claims,
            _ => StorageObservationReadKind.Coherent,
        };
        var context = await contexts.LoadAsync(targetId, ct);
        if (context is null)
        {
            metrics?.RecordRead(StorageObservationReadOutcome.Unavailable, readKind);
            return new KubernetesStorageDiscoveryUnavailable(targetId);
        }

        if (options.Enabled && await store.LoadFreshSnapshotAsync(context, ct) is { } shared)
        {
            metrics?.RecordRead(StorageObservationReadOutcome.Shared, readKind);
            return shared;
        }

        var inventory = await native.ReadKindAsync(context, kind, ct);
        if (inventory is null || !await contexts.IsCurrentAsync(context, ct))
        {
            metrics?.RecordRead(StorageObservationReadOutcome.Unavailable, readKind);
            return new KubernetesStorageDiscoveryUnavailable(context.DeploymentTargetId);
        }

        metrics?.RecordRead(StorageObservationReadOutcome.Live, readKind);
        return new StorageObservationSnapshot(
            context.DeploymentTargetId,
            context.ConnectionVersion,
            null,
            clock.GetUtcNow().UtcDateTime,
            inventory.Complete,
            inventory.StorageClasses,
            inventory.PersistentVolumes,
            inventory.PersistentVolumeClaims);
    }

    public async Task<ReadStorageObservationOutcome> ReadAsync(string targetId, CancellationToken ct = default)
    {
        var context = await contexts.LoadAsync(targetId, ct);
        if (context is null)
        {
            metrics?.RecordRead(StorageObservationReadOutcome.Unavailable);
            return new KubernetesStorageDiscoveryUnavailable(targetId);
        }

        // Pin the floor before any live native work; later hints must invalidate older evidence.
        var control = await store.LoadControlAsync(context.DeploymentTargetId, ct);
        var version = control?.ObservationVersion > 0 ? control.ObservationVersion : (long?)null;
        if (options.Enabled && await store.LoadFreshSnapshotAsync(context, ct) is { } shared)
        {
            metrics?.RecordRead(StorageObservationReadOutcome.Shared);
            return shared;
        }

        var inventory = await native.ReadAsync(context, ct);
        if (inventory is null || !await contexts.IsCurrentAsync(context, ct))
        {
            metrics?.RecordRead(StorageObservationReadOutcome.Unavailable);
            return new KubernetesStorageDiscoveryUnavailable(context.DeploymentTargetId);
        }

        metrics?.RecordRead(StorageObservationReadOutcome.Live);
        return new StorageObservationSnapshot(
            context.DeploymentTargetId,
            context.ConnectionVersion,
            version,
            clock.GetUtcNow().UtcDateTime,
            inventory.Complete,
            inventory.StorageClasses,
            inventory.PersistentVolumes,
            inventory.PersistentVolumeClaims);
    }
}
