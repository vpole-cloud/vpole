// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Discovery.Observation;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application.Discovery;

public union DiscoverKubernetesPersistentVolumeClaimsOutcome(
    IReadOnlyCollection<KubernetesPersistentVolumeClaimDto>,
    KubernetesStorageDiscoveryUnavailable);

public delegate Task<DiscoverKubernetesPersistentVolumeClaimsOutcome> DiscoverKubernetesPersistentVolumeClaims(
    string deploymentTargetId,
    CancellationToken cancellationToken = default);

internal sealed class KubernetesDiscoverPersistentVolumeClaims(
    StorageObservationReader reader
)
{
    public async Task<DiscoverKubernetesPersistentVolumeClaimsOutcome> InvokeAsync(
        string deploymentTargetId,
        CancellationToken cancellationToken = default)
    {
        return await reader.ReadLegacyAsync(deploymentTargetId, StorageInventoryKind.Claims, cancellationToken) switch
        {
            StorageObservationSnapshot snapshot => snapshot.PersistentVolumeClaims
                .Select(item => new KubernetesPersistentVolumeClaimDto(
                    item.Name,
                    item.Namespace,
                    item.Capacity,
                    item.RequestedCapacity,
                    item.Status,
                    item.StorageClassName,
                    item.VolumeName,
                    Owner(item.Owner)))
                .ToArray(),
            KubernetesStorageDiscoveryUnavailable unavailable => unavailable,
        };
    }

    private static KubernetesStorageOwnerDto Owner(StorageOwnerObservation owner) => new(
        owner.TenantId,
        owner.ResourceGroupId,
        owner.ResourceType,
        owner.ResourceId,
        owner.ResourceName);
}
