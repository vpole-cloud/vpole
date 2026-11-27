// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using System.Net;
using System.Net.Sockets;
using k8s.Models;

namespace Pole.Features.Functions.Runtime.Endpoints;

public readonly record struct FunctionServiceKey(
    string DeploymentTargetId,
    string Namespace,
    string ServiceName
);

public sealed record FunctionEndpoint(
    string Address,
    int Port,
    string? TargetUid
);

public sealed record FunctionEndpointSnapshot(
    FunctionServiceKey Service,
    ImmutableArray<FunctionEndpoint> Endpoints,
    DateTimeOffset UpdatedAtUtc
)
{
    public bool HasReadyEndpoints => !Endpoints.IsDefaultOrEmpty;
}

public interface IFunctionEndpointRegistry
{
    FunctionEndpointSnapshot GetSnapshot(FunctionServiceKey service);
    void ReplaceTarget(string deploymentTargetId, IEnumerable<V1EndpointSlice> slices);
    void ApplySlice(string deploymentTargetId, V1EndpointSlice slice);

    void DeleteSlice(
        string deploymentTargetId,
        string @namespace,
        string sliceName);

    void RemoveTarget(string deploymentTargetId);
}

public sealed class FunctionEndpointRegistry(
    TimeProvider timeProvider
) : IFunctionEndpointRegistry
{
    private readonly object writeLock = new();

    private RegistryState state = new(
        ImmutableDictionary.Create<string, SliceState>(StringComparer.Ordinal),
        ImmutableDictionary<FunctionServiceKey, FunctionEndpointSnapshot>.Empty);

    public FunctionEndpointSnapshot GetSnapshot(FunctionServiceKey service) =>
        Volatile.Read(ref state).Snapshots.TryGetValue(service, out var snapshot) ? snapshot : Empty(service);

    public void ReplaceTarget(string deploymentTargetId, IEnumerable<V1EndpointSlice> endpointSlices)
    {
        ArgumentNullException.ThrowIfNull(endpointSlices);
        var slices = endpointSlices.Select(slice => TryProjectSlice(deploymentTargetId, slice))
            .Where(projected => projected is not null)
            .ToImmutableDictionary(
                projected => projected!.Value.Key,
                projected => projected!.Value.State,
                StringComparer.Ordinal);
        lock (writeLock)
        {
            var current = Volatile.Read(ref state);
            var retained = current.Slices.RemoveRange(
                current.Slices.Keys.Where(key => key.StartsWith($"{deploymentTargetId}/", StringComparison.Ordinal)));
            Volatile.Write(ref state, BuildState(retained.SetItems(slices)));
        }
    }

    public void ApplySlice(string deploymentTargetId, V1EndpointSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        var projected = TryProjectSlice(deploymentTargetId, slice);
        if (projected is null)
        {
            if (slice.Metadata is { NamespaceProperty: { Length: > 0 } @namespace, Name: { Length: > 0 } name })
                DeleteSlice(deploymentTargetId, @namespace, name);
            return;
        }

        lock (writeLock)
        {
            var current = Volatile.Read(ref state);
            Volatile.Write(ref state, BuildState(current.Slices.SetItem(projected.Value.Key, projected.Value.State)));
        }
    }

    public void DeleteSlice(
        string deploymentTargetId,
        string @namespace,
        string sliceName)
    {
        lock (writeLock)
        {
            var current = Volatile.Read(ref state);
            var updated = current.Slices.Remove(SliceKey(deploymentTargetId, @namespace, sliceName));
            if (updated != current.Slices)
                Volatile.Write(ref state, BuildState(updated));
        }
    }

    public void RemoveTarget(string deploymentTargetId)
    {
        lock (writeLock)
        {
            var current = Volatile.Read(ref state);
            var updated = current.Slices.RemoveRange(
                current.Slices.Keys.Where(key => key.StartsWith($"{deploymentTargetId}/", StringComparison.Ordinal)));
            if (updated != current.Slices)
                Volatile.Write(ref state, BuildState(updated));
        }
    }

    private RegistryState BuildState(ImmutableDictionary<string, SliceState> slices)
    {
        var now = timeProvider.GetUtcNow();
        var snapshots = slices.Values
            .GroupBy(slice => slice.Service)
            .ToImmutableDictionary(
                group => group.Key,
                group => new FunctionEndpointSnapshot(
                    group.Key,
                    group.SelectMany(slice => slice.Endpoints)
                        .DistinctBy(endpoint => (endpoint.Address, endpoint.Port, endpoint.TargetUid))
                        .OrderBy(endpoint => endpoint.Address, StringComparer.Ordinal)
                        .ThenBy(endpoint => endpoint.Port)
                        .ToImmutableArray(),
                    now));
        return new RegistryState(slices, snapshots);
    }

    private static (string Key, SliceState State)? TryProjectSlice(string deploymentTargetId, V1EndpointSlice slice)
    {
        var @namespace = slice.Metadata?.NamespaceProperty;
        var sliceName = slice.Metadata?.Name;
        if (string.IsNullOrWhiteSpace(@namespace) || string.IsNullOrWhiteSpace(sliceName))
            return null;
        if (slice.Metadata?.Labels is null ||
            !slice.Metadata.Labels.TryGetValue("kubernetes.io/service-name", out var serviceName) ||
            string.IsNullOrWhiteSpace(serviceName))
            return null;

        var service = new FunctionServiceKey(deploymentTargetId, @namespace, serviceName);
        return (SliceKey(deploymentTargetId, @namespace, sliceName),
            new SliceState(service, Project(slice, @namespace)));
    }

    private static ImmutableArray<FunctionEndpoint> Project(V1EndpointSlice slice, string sliceNamespace)
    {
        var port = slice.Ports?.FirstOrDefault(candidate => candidate.Name == "agent" && candidate.Protocol == "TCP")
            ?.Port;
        if (port is null)
            return [];

        return (slice.Endpoints ?? []).Where(endpoint => endpoint.Conditions?.Ready == true &&
                endpoint.Conditions.Terminating != true && endpoint.TargetRef is
                {
                    Kind: "Pod",
                    Uid: { Length: > 0 },
                } target &&
                (string.IsNullOrWhiteSpace(target.NamespaceProperty) || string.Equals(
                    target.NamespaceProperty,
                    sliceNamespace,
                    StringComparison.Ordinal)))
            .SelectMany(endpoint => (endpoint.Addresses ?? []).Where(IsValidPodAddress)
                .Select(address => new FunctionEndpoint(address, port.Value, endpoint.TargetRef!.Uid)))
            .ToImmutableArray();
    }

    private static bool IsValidPodAddress(string address)
    {
        if (!IPAddress.TryParse(address, out var ipAddress) || IPAddress.IsLoopback(ipAddress))
            return false;
        if (ipAddress.Equals(IPAddress.Any) || ipAddress.Equals(IPAddress.IPv6Any) ||
            ipAddress.Equals(IPAddress.Broadcast))
            return false;
        if (ipAddress.AddressFamily == AddressFamily.InterNetworkV6)
            return !ipAddress.IsIPv6LinkLocal && !ipAddress.IsIPv6Multicast;

        var bytes = ipAddress.GetAddressBytes();
        return bytes[0] is not 0 and not 127 && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }

    private FunctionEndpointSnapshot Empty(FunctionServiceKey service) => new(service, [], timeProvider.GetUtcNow());

    private static string SliceKey(
        string deploymentTargetId,
        string @namespace,
        string sliceName) => $"{deploymentTargetId}/{@namespace}/{sliceName}";

    private sealed record SliceState(
        FunctionServiceKey Service,
        ImmutableArray<FunctionEndpoint> Endpoints
    );

    private sealed record RegistryState(
        ImmutableDictionary<string, SliceState> Slices,
        ImmutableDictionary<FunctionServiceKey, FunctionEndpointSnapshot> Snapshots
    );
}
