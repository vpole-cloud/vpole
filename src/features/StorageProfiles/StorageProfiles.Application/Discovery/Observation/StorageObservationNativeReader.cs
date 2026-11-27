// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using k8s;
using k8s.Autorest;
using Pole.Features.DeploymentTargets.Contracts;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Observation;

namespace Pole.Features.StorageProfiles.Application.Discovery.Observation;

internal enum StorageInventoryKind
{
    Classes,
    Volumes,
    Claims,
}

public sealed class StorageObservationNativeReader(
    IKubernetesClientProvider clients,
    StorageObservationContextLoader contexts,
    IObservationReadBudget budget,
    StorageObservationOptions options
)
{
    internal async Task<IKubernetes> CurrentClientAsync(StorageObservationContext context, CancellationToken ct)
    {
        if (!await contexts.IsCurrentAsync(context, ct))
            throw new DeploymentTargetUnavailableException(
                context.DeploymentTargetId,
                DeploymentTargetUnavailableReason.ConnectionUnavailable);
        var client = await clients.GetAsync(context.DeploymentTargetId, ct);
        if (!await contexts.IsCurrentAsync(context, ct))
            throw new DeploymentTargetUnavailableException(
                context.DeploymentTargetId,
                DeploymentTargetUnavailableReason.ConnectionUnavailable);
        return client;
    }

    internal Task<T> ReadNativeAsync<T>(
        StorageObservationContext context,
        Func<IKubernetes, CancellationToken, Task<T>> read,
        CancellationToken ct) => budget.ReadAsync(
        async token =>
        {
            var result = await read(await CurrentClientAsync(context, token), token);
            if (!await contexts.IsCurrentAsync(context, token))
                throw new DeploymentTargetUnavailableException(
                    context.DeploymentTargetId,
                    DeploymentTargetUnavailableReason.ConnectionUnavailable);
            return result;
        },
        ct);

    public async Task<StorageInventoryObservation?> ReadAsync(
        StorageObservationContext context,
        CancellationToken ct = default)
    {
        try
        {
            return await budget.ReadAsync(
                async token =>
                {
                    var client = await CurrentClientAsync(context, token);
                    var classes = await PagesAsync(async continuation =>
                    {
                        var page = await client.StorageV1.ListStorageClassAsync(
                            limit: 100,
                            continueParameter: continuation,
                            cancellationToken: token);
                        return (page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
                    });
                    var volumes = await PagesAsync(async continuation =>
                    {
                        var page = await client.CoreV1.ListPersistentVolumeAsync(
                            limit: 100,
                            continueParameter: continuation,
                            cancellationToken: token);
                        return (page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
                    });
                    var claims = await PagesAsync(async continuation =>
                    {
                        var page = await client.CoreV1.ListPersistentVolumeClaimForAllNamespacesAsync(
                            limit: 100,
                            continueParameter: continuation,
                            cancellationToken: token);
                        return (page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
                    });
                    if (classes is null || volumes is null || claims is null ||
                        !await contexts.IsCurrentAsync(context, token))
                        return null;
                    return StorageObservationValueMappings.Sanitize(
                        new StorageInventoryObservation(
                            classes.Select(item =>
                                    StorageObservationValueMappings.From(
                                        KubernetesStorageDiscovery.MapStorageClass(item)))
                                .ToArray(),
                            volumes.Select(item =>
                                    StorageObservationValueMappings.From(
                                        KubernetesStorageDiscovery.MapPersistentVolume(item)))
                                .ToArray(),
                            claims.Select(item =>
                                    StorageObservationValueMappings.From(
                                        KubernetesStorageDiscovery.MapPersistentVolumeClaim(item)))
                                .ToArray(),
                            true),
                        options);
                },
                ct);
        }
        catch (DeploymentTargetUnavailableException)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpOperationException or KubernetesException or HttpRequestException
                                       or IOException or JsonException || (ex is OperationCanceledException &&
                                       !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    internal async Task<IReadOnlySet<string>?> ReadNamesAsync(StorageObservationContext context, CancellationToken ct)
    {
        try
        {
            return await budget.ReadAsync(
                async token =>
                {
                    var client = await CurrentClientAsync(context, token);
                    var classes = await PagesAsync(async next =>
                    {
                        var page = await client.StorageV1.ListStorageClassAsync(
                            limit: 100,
                            continueParameter: next,
                            cancellationToken: token);
                        return (page.Items.ToArray(), page.Metadata?.ResourceVersion, page.Metadata?.ContinueProperty);
                    });
                    return classes is null || !await contexts.IsCurrentAsync(context, token)
                        ? null
                        : (IReadOnlySet<string>)classes.Select(item => item.Metadata.Name)
                            .Where(name => !string.IsNullOrEmpty(name))
                            .ToHashSet(StringComparer.Ordinal);
                },
                ct);
        }
        catch (DeploymentTargetUnavailableException)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpOperationException or KubernetesException or HttpRequestException
                                       or IOException or JsonException || (ex is OperationCanceledException &&
                                       !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    internal async Task<StorageInventoryObservation?> ReadKindAsync(
        StorageObservationContext context,
        StorageInventoryKind kind,
        CancellationToken ct)
    {
        try
        {
            return await budget.ReadAsync(
                async token =>
                {
                    var client = await CurrentClientAsync(context, token);
                    StorageInventoryObservation? inventory;
                    switch (kind)
                    {
                        case StorageInventoryKind.Classes:
                            var classes = await PagesAsync(async next =>
                            {
                                var page = await client.StorageV1.ListStorageClassAsync(
                                    limit: 100,
                                    continueParameter: next,
                                    cancellationToken: token);
                                return (page.Items.ToArray(), page.Metadata?.ResourceVersion,
                                    page.Metadata?.ContinueProperty);
                            });
                            inventory = classes is null
                                ? null
                                : new StorageInventoryObservation(
                                    classes.Select(item =>
                                            StorageObservationValueMappings.From(
                                                KubernetesStorageDiscovery.MapStorageClass(item)))
                                        .ToArray(),
                                    [],
                                    [],
                                    true);
                            break;
                        case StorageInventoryKind.Volumes:
                            var volumes = await PagesAsync(async next =>
                            {
                                var page = await client.CoreV1.ListPersistentVolumeAsync(
                                    limit: 100,
                                    continueParameter: next,
                                    cancellationToken: token);
                                return (page.Items.ToArray(), page.Metadata?.ResourceVersion,
                                    page.Metadata?.ContinueProperty);
                            });
                            inventory = volumes is null
                                ? null
                                : new StorageInventoryObservation(
                                    [],
                                    volumes.Select(item =>
                                            StorageObservationValueMappings.From(
                                                KubernetesStorageDiscovery.MapPersistentVolume(item)))
                                        .ToArray(),
                                    [],
                                    true);
                            break;
                        case StorageInventoryKind.Claims:
                            var claims = await PagesAsync(async next =>
                            {
                                var page = await client.CoreV1.ListPersistentVolumeClaimForAllNamespacesAsync(
                                    limit: 100,
                                    continueParameter: next,
                                    cancellationToken: token);
                                return (page.Items.ToArray(), page.Metadata?.ResourceVersion,
                                    page.Metadata?.ContinueProperty);
                            });
                            inventory = claims is null
                                ? null
                                : new StorageInventoryObservation(
                                    [],
                                    [],
                                    claims.Select(item =>
                                            StorageObservationValueMappings.From(
                                                KubernetesStorageDiscovery.MapPersistentVolumeClaim(item)))
                                        .ToArray(),
                                    true);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(kind));
                    }

                    return inventory is null || !await contexts.IsCurrentAsync(context, token)
                        ? null
                        : StorageObservationValueMappings.Sanitize(inventory, options);
                },
                ct);
        }
        catch (DeploymentTargetUnavailableException)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpOperationException or KubernetesException or HttpRequestException
                                       or IOException or JsonException || (ex is OperationCanceledException &&
                                       !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private async Task<T[]?> PagesAsync<T>(Func<string?, Task<(T[] Items, string? Version, string? Next)>> read)
    {
        var items = new List<T>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? next = null;
        string? version = null;
        do
        {
            var page = await read(next);
            if (string.IsNullOrEmpty(page.Version) || (version is not null && page.Version != version))
                return null;
            version = page.Version;
            items.AddRange(page.Items);
            if (items.Count > options.MaximumItemsPerKind)
                return null;
            next = page.Next;
            if (!string.IsNullOrEmpty(next) && (!seen.Add(next) || seen.Count > 100))
                return null;
        } while (!string.IsNullOrEmpty(next));

        return items.ToArray();
    }
}
