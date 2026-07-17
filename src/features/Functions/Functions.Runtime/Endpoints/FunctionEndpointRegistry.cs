using System.Collections.Concurrent;
using System.Collections.Immutable;
using k8s.Models;

namespace Pole.Features.Functions.Runtime.Endpoints;

public readonly record struct FunctionServiceKey(string Namespace, string ServiceName);

public sealed record FunctionEndpoint(string Address, int Port, string? TargetUid);

public sealed record FunctionEndpointSnapshot(
    FunctionServiceKey Service,
    ImmutableArray<FunctionEndpoint> Endpoints,
    DateTimeOffset UpdatedAtUtc)
{
    public bool HasReadyEndpoints => !Endpoints.IsDefaultOrEmpty;
}

public interface IFunctionEndpointRegistry
{
    FunctionEndpointSnapshot GetSnapshot(FunctionServiceKey service);
    void ApplySlice(V1EndpointSlice slice);
    void DeleteSlice(string @namespace, string sliceName);
}

public sealed class FunctionEndpointRegistry(TimeProvider timeProvider) : IFunctionEndpointRegistry
{
    private sealed record SliceState(
        FunctionServiceKey Service,
        ImmutableArray<FunctionEndpoint> Endpoints);

    private readonly ConcurrentDictionary<string, SliceState> slices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<FunctionServiceKey, FunctionEndpointSnapshot> snapshots = [];

    public FunctionEndpointSnapshot GetSnapshot(FunctionServiceKey service) =>
        snapshots.TryGetValue(service, out var snapshot)
            ? snapshot
            : Empty(service);

    public void ApplySlice(V1EndpointSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);
        var @namespace = slice.Metadata?.NamespaceProperty;
        var sliceName = slice.Metadata?.Name;
        if (string.IsNullOrWhiteSpace(@namespace) || string.IsNullOrWhiteSpace(sliceName))
            return;
        if (slice.Metadata?.Labels is null ||
            !slice.Metadata.Labels.TryGetValue("kubernetes.io/service-name", out var serviceName) ||
            string.IsNullOrWhiteSpace(serviceName))
            return;

        var service = new FunctionServiceKey(@namespace, serviceName);
        slices.TryGetValue(SliceKey(@namespace, sliceName), out var previous);
        slices[SliceKey(@namespace, sliceName)] = new SliceState(service, Project(slice));
        if (previous is not null && previous.Service != service)
            Rebuild(previous.Service);
        Rebuild(service);
    }

    public void DeleteSlice(string @namespace, string sliceName)
    {
        if (slices.TryRemove(SliceKey(@namespace, sliceName), out var removed))
            Rebuild(removed.Service);
    }

    private void Rebuild(FunctionServiceKey service)
    {
        var endpoints = slices.Values
            .Where(slice => slice.Service == service)
            .SelectMany(slice => slice.Endpoints)
            .DistinctBy(endpoint => (endpoint.Address, endpoint.Port, endpoint.TargetUid))
            .OrderBy(endpoint => endpoint.Address, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.Port)
            .ToImmutableArray();
        snapshots[service] = new FunctionEndpointSnapshot(service, endpoints, timeProvider.GetUtcNow());
    }

    private static ImmutableArray<FunctionEndpoint> Project(V1EndpointSlice slice)
    {
        var port = slice.Ports?
            .FirstOrDefault(candidate => candidate.Name == "agent" && candidate.Protocol == "TCP")?
            .Port;
        if (port is null)
            return [];

        return (slice.Endpoints ?? [])
            .Where(endpoint => endpoint.Conditions?.Ready == true && endpoint.Conditions.Terminating != true)
            .SelectMany(endpoint => (endpoint.Addresses ?? []).Select(address => new FunctionEndpoint(
                address,
                port.Value,
                endpoint.TargetRef?.Uid)))
            .ToImmutableArray();
    }

    private FunctionEndpointSnapshot Empty(FunctionServiceKey service) =>
        new(service, [], timeProvider.GetUtcNow());

    private static string SliceKey(string @namespace, string sliceName) => $"{@namespace}/{sliceName}";
}
