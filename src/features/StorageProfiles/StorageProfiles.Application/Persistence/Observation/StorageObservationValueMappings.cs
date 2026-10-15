using MongoDB.Bson;
using Pole.Features.StorageProfiles.Application.Common;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application.Persistence.Observation;

internal static class StorageObservationValueMappings
{
    internal static StorageObservationSnapshot Load(StorageObservationSnapshotDocument snapshot) => new(snapshot.Id,
        snapshot.ConnectionVersion,
        snapshot.ObservationVersion,
        snapshot.CheckedAtUtc,
        snapshot.Inventory.Complete,
        snapshot.Inventory.StorageClasses,
        snapshot.Inventory.PersistentVolumes,
        snapshot.Inventory.PersistentVolumeClaims);

    internal static StorageInventoryObservation? Sanitize(StorageInventoryObservation inventory,
        StorageObservationOptions options)
    {
        if (!inventory.Complete || inventory.StorageClasses.Length > options.MaximumItemsPerKind ||
            inventory.PersistentVolumes.Length > options.MaximumItemsPerKind ||
            inventory.PersistentVolumeClaims.Length > options.MaximumItemsPerKind)
            return null;
        var safe = new StorageInventoryObservation(
            inventory.StorageClasses
                .Select(item => item with
                {
                    Name = Name(item.Name),
                    Provisioner = Provisioner(item.Provisioner),
                    ReclaimPolicy = Reclaim(item.ReclaimPolicy),
                    VolumeBindingMode = Binding(item.VolumeBindingMode)
                }).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            inventory.PersistentVolumes.Select(item => item with
            {
                Name = Name(item.Name),
                Capacity = Quantity(item.Capacity),
                Status = VolumePhase(item.Status),
                StorageClassName = OptionalName(item.StorageClassName),
                BoundClaimNamespace = OptionalName(item.BoundClaimNamespace),
                BoundClaimName = OptionalName(item.BoundClaimName),
                ReclaimPolicy = Reclaim(item.ReclaimPolicy),
                Owner = Owner(item.Owner)
            }).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            inventory.PersistentVolumeClaims.Select(item => item with
            {
                Name = Name(item.Name),
                Namespace = Name(item.Namespace),
                Capacity = Quantity(item.Capacity),
                RequestedCapacity = Quantity(item.RequestedCapacity),
                Status = ClaimPhase(item.Status),
                StorageClassName = OptionalName(item.StorageClassName),
                VolumeName = OptionalName(item.VolumeName),
                Owner = Owner(item.Owner)
            }).OrderBy(item => item.Namespace, StringComparer.Ordinal)
                .ThenBy(item => item.Name, StringComparer.Ordinal).ToArray(),
            true);
        return safe.ToBson().Length <= options.MaximumSnapshotBytes ? safe : null;
    }

    internal static StorageClassObservation From(KubernetesStorageClassDto item) => new(item.Name,
        item.Provisioner,
        item.IsDefault,
        item.ReclaimPolicy,
        item.VolumeBindingMode,
        item.AllowVolumeExpansion);

    internal static PersistentVolumeObservation From(KubernetesPersistentVolumeDto item) => new(item.Name,
        item.Capacity,
        item.Status,
        item.StorageClassName,
        item.BoundClaimNamespace,
        item.BoundClaimName,
        item.ReclaimPolicy,
        From(item.Owner));

    internal static PersistentVolumeClaimObservation From(KubernetesPersistentVolumeClaimDto item) => new(item.Name,
        item.Namespace,
        item.Capacity,
        item.RequestedCapacity,
        item.Status,
        item.StorageClassName,
        item.VolumeName,
        From(item.Owner));

    private static StorageOwnerObservation From(KubernetesStorageOwnerDto item) => new(item.TenantId,
        item.ResourceGroupId,
        item.ResourceType,
        item.ResourceId,
        item.ResourceName);

    private static StorageOwnerObservation Owner(StorageOwnerObservation item) => new(
        StorageOwnerValueRules.MongoIdentity(item.TenantId),
        StorageOwnerValueRules.MongoIdentity(item.ResourceGroupId),
        StorageOwnerValueRules.OptionalText(item.ResourceType, 253),
        StorageOwnerValueRules.ResourceIdentity(item.ResourceId),
        StorageOwnerValueRules.OptionalText(item.ResourceName, 253));

    private static string? Reclaim(string? value) => value is "Delete" or "Retain" or "Recycle" ? value : null;
    private static string? Binding(string? value) => value is "Immediate" or "WaitForFirstConsumer" ? value : null;

    private static string? VolumePhase(string? value) =>
        value is "Pending" or "Available" or "Bound" or "Released" or "Failed" ? value : null;

    private static string? ClaimPhase(string? value) => value is "Pending" or "Bound" or "Lost" ? value : null;

    private static string? Quantity(string? value) => value is null ? null :
        value.Length <= 64 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '+' or '-') ? value : null;

    private static string? OptionalName(string? value) => string.IsNullOrEmpty(value) ? null : Name(value);

    private static string Name(string value) =>
        value.Length is > 0 and <= 253 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_')
            ? value
            : throw new ArgumentException("Invalid storage metadata name.");

    private static string Provisioner(string value)
    {
        // Kubernetes accepts a DNS prefix (253), '/' and a qualified name (63).
        if (value.Length is 0 or > 317)
            throw new ArgumentException("Invalid storage provisioner.");
        var parts = value.Split('/');
        var name = parts[^1];
        if (parts.Length > 2 || name.Length is 0 or > 63 || !char.IsAsciiLetterOrDigit(name[0]) ||
            !char.IsAsciiLetterOrDigit(name[^1])
            || !name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_'))
            throw new ArgumentException("Invalid storage provisioner.");
        // StorageClass validation lowercases the provisioner before validating its prefix.
        if (parts.Length == 2 && (parts[0].Length is 0 or > 253 || !parts[0].Split('.').All(label => label.Length > 0
                && char.IsAsciiLetterOrDigit(label[0]) && char.IsAsciiLetterOrDigit(label[^1])
                && label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))))
            throw new ArgumentException("Invalid storage provisioner.");
        return value;
    }
}
