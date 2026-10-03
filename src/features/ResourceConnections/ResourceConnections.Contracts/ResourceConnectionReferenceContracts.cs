using System.Collections.Immutable;
using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ResourceConnections.Contracts;

public sealed record ResourceConnectionValueReference(
    string ConnectionId,
    string ValueKind,
    string? ConfigurationKey = null);

public sealed record ResourceConnectionReferencesAdmitted;
public sealed record ResourceConnectionReferencesUnavailable(
    string ConnectionId,
    string ReasonCode);
public sealed record ResolvedResourceConnectionValue(
    ResourceConnectionValueReference Reference,
    string Value);
public sealed record ResolvedResourceConnectionValues(ImmutableArray<ResolvedResourceConnectionValue> Values);

public union AdmitResourceConnectionReferencesOutcome(
    ResourceConnectionReferencesAdmitted,
    ResourceConnectionReferencesUnavailable);
public union ResolveResourceConnectionReferencesOutcome(
    ResolvedResourceConnectionValues,
    ResourceConnectionReferencesUnavailable);

/// <summary>Fences current source ownership, provider fields and every desired source placement in the caller transaction.</summary>
public delegate Task<AdmitResourceConnectionReferencesOutcome> AdmitResourceConnectionReferences(
    string tenantId,
    ResourceTypeId sourceType,
    string sourceId,
    IReadOnlyCollection<ResourceConnectionValueReference> references,
    IClientSessionHandle session,
    CancellationToken ct);

/// <summary>Resolves non-secret fields for one exact owned source deployment. Policy application remains a separate state.</summary>
public delegate Task<ResolveResourceConnectionReferencesOutcome> ResolveResourceConnectionReferences(
    string tenantId,
    ResourceTypeId sourceType,
    string sourceId,
    string deploymentId,
    IReadOnlyCollection<ResourceConnectionValueReference> references,
    CancellationToken ct);
