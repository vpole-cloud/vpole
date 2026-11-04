using System.Collections.Immutable;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Dns;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Domain;

public sealed record ResourceDeployment(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string TenantId,
    string ResourceType,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string ResourceId,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string ResourceGroupId,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string IsolationScopeId,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string RuntimeCellId,
    string PlacementKey,
    ResourcePlacementCapability PlacementCapability,
    ResolvedResourcePlacement ResolvedPlacement,
    long ResourceRevision,
    int FixedReplicas,
    long Revision,
    long DesiredGeneration,
    ResourceDeploymentOperation DesiredOperation,
    long ObservedGeneration,
    ResourceDeploymentState State,
    ImmutableList<ResourceDeploymentEndpoint> Endpoints,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    long ReferenceFence)
{
    public ResourceDeployment RequestReconciliation(
        ResolvedResourcePlacement resolvedPlacement,
        bool runtimeCellReady,
        DateTime requestedAtUtc,
        long? resourceRevision = null,
        int? fixedReplicas = null)
    {
        ArgumentNullException.ThrowIfNull(resolvedPlacement);
        EnsureSamePlacement(resolvedPlacement);
        EnsureNotDeleted();

        var normalizedRequestedAtUtc = ResourceDeploymentValueGuard.UpdatedUtc(
            requestedAtUtc,
            UpdatedAtUtc,
            nameof(requestedAtUtc));
        var nextResourceRevision = resourceRevision is null
            ? ResourceRevision
            : ResourceDeploymentValueGuard.Positive(
                resourceRevision.Value,
                nameof(resourceRevision));
        if (nextResourceRevision < ResourceRevision)
        {
            throw new InvalidOperationException(
                "A resource deployment cannot move to an older logical resource revision.");
        }
        var nextFixedReplicas = fixedReplicas is null
            ? FixedReplicas
            : ResourceDeploymentValueGuard.PositiveInt(
                fixedReplicas.Value,
                nameof(fixedReplicas));
        var nextGeneration = checked(DesiredGeneration + 1);
        var nextState = State is ReconcilingResourceDeploymentState
            ? State
            : runtimeCellReady
                ? new PendingResourceDeploymentState(
                    nextGeneration,
                    ResourceDeploymentOperation.Apply,
                    AttemptCount: 0,
                    normalizedRequestedAtUtc)
                : new WaitingForRuntimeCellResourceDeploymentState(
                    nextGeneration,
                    normalizedRequestedAtUtc);

        return this with
        {
            ResolvedPlacement = resolvedPlacement,
            ResourceRevision = nextResourceRevision,
            FixedReplicas = nextFixedReplicas,
            Revision = checked(Revision + 1),
            DesiredGeneration = nextGeneration,
            DesiredOperation = ResourceDeploymentOperation.Apply,
            State = nextState,
            UpdatedAtUtc = normalizedRequestedAtUtc
        };
    }

    public ResourceDeployment ConfigurePrepared(
        ResolvedResourcePlacement placement,
        DateTime requestedAtUtc,
        long resourceRevision,
        int fixedReplicas)
    {
        if (State is not PreparedResourceDeploymentState || DesiredOperation != ResourceDeploymentOperation.Apply)
            throw new InvalidOperationException("Only a prepared deployment can accept deferred configuration.");
        EnsureSamePlacement(placement);
        var revision = ResourceDeploymentValueGuard.Positive(resourceRevision, nameof(resourceRevision));
        if (revision < ResourceRevision)
            throw new InvalidOperationException("A prepared deployment cannot move to an older resource revision.");

        var generation = checked(DesiredGeneration + 1);
        return this with
        {
            ResolvedPlacement = placement,
            ResourceRevision = revision,
            FixedReplicas = ResourceDeploymentValueGuard.PositiveInt(fixedReplicas, nameof(fixedReplicas)),
            Revision = checked(Revision + 1),
            DesiredGeneration = generation,
            State = new PreparedResourceDeploymentState(generation),
            UpdatedAtUtc = ResourceDeploymentValueGuard.UpdatedUtc(requestedAtUtc, UpdatedAtUtc, nameof(requestedAtUtc))
        };
    }

    public ResourceDeployment MarkRuntimeCellReady(DateTime requestedAtUtc)
    {
        if (State is not WaitingForRuntimeCellResourceDeploymentState)
            return this;
        if (DesiredOperation != ResourceDeploymentOperation.Apply)
            throw new InvalidOperationException("A deployment pending deletion cannot wait for its runtime cell.");

        var normalizedRequestedAtUtc = ResourceDeploymentValueGuard.UpdatedUtc(
            requestedAtUtc,
            UpdatedAtUtc,
            nameof(requestedAtUtc));
        return this with
        {
            Revision = checked(Revision + 1),
            State = new PendingResourceDeploymentState(
                DesiredGeneration,
                DesiredOperation,
                AttemptCount: 0,
                normalizedRequestedAtUtc),
            UpdatedAtUtc = normalizedRequestedAtUtc
        };
    }

    public ResourceDeployment RequestDeletion(
        DateTime requestedAtUtc,
        long? resourceRevision = null)
    {
        if (State is DeletedResourceDeploymentState)
            return this;
        if (DesiredOperation == ResourceDeploymentOperation.Delete &&
            State is not FailedResourceDeploymentState)
        {
            return resourceRevision is null
                ? this
                : AdvanceResourceRevision(resourceRevision.Value, requestedAtUtc);
        }

        var normalizedRequestedAtUtc = ResourceDeploymentValueGuard.UpdatedUtc(
            requestedAtUtc,
            UpdatedAtUtc,
            nameof(requestedAtUtc));
        var nextGeneration = checked(DesiredGeneration + 1);
        var nextResourceRevision = resourceRevision is null
            ? ResourceRevision
            : ResourceDeploymentValueGuard.Positive(
                resourceRevision.Value,
                nameof(resourceRevision));
        if (nextResourceRevision < ResourceRevision)
        {
            throw new InvalidOperationException(
                "A resource deployment cannot move to an older logical resource revision.");
        }
        return this with
        {
            ResourceRevision = nextResourceRevision,
            Revision = checked(Revision + 1),
            DesiredGeneration = nextGeneration,
            DesiredOperation = ResourceDeploymentOperation.Delete,
            State = State is ReconcilingResourceDeploymentState
                ? State
                : new PendingResourceDeploymentState(
                    nextGeneration,
                    ResourceDeploymentOperation.Delete,
                    AttemptCount: 0,
                    normalizedRequestedAtUtc),
            UpdatedAtUtc = normalizedRequestedAtUtc
        };
    }

    /// <summary>
    /// Stamps this physical deployment with a newer logical desired-state revision
    /// without requesting another physical reconciliation generation.
    /// </summary>
    public ResourceDeployment AdvanceResourceRevision(
        long resourceRevision,
        DateTime requestedAtUtc)
    {
        var normalizedResourceRevision = ResourceDeploymentValueGuard.Positive(
            resourceRevision,
            nameof(resourceRevision));
        if (normalizedResourceRevision == ResourceRevision)
            return this;
        if (normalizedResourceRevision < ResourceRevision)
        {
            throw new InvalidOperationException(
                "A resource deployment cannot move to an older logical resource revision.");
        }

        var normalizedRequestedAtUtc = ResourceDeploymentValueGuard.UpdatedUtc(
            requestedAtUtc,
            UpdatedAtUtc,
            nameof(requestedAtUtc));
        return this with
        {
            ResourceRevision = normalizedResourceRevision,
            Revision = checked(Revision + 1),
            UpdatedAtUtc = normalizedRequestedAtUtc
        };
    }

    // Recheck applied infrastructure without minting a new provider release. WebServices
    // resolves its durable release by this exact desired generation and resource revision.
    public ResourceDeployment RequestVerification(DateTime requestedAtUtc)
    {
        if (State is not AppliedResourceDeploymentState ||
            DesiredOperation != ResourceDeploymentOperation.Apply)
        {
            throw new InvalidOperationException("Only an applied deployment can request verification.");
        }

        var normalizedRequestedAtUtc = ResourceDeploymentValueGuard.UpdatedUtc(
            requestedAtUtc,
            UpdatedAtUtc,
            nameof(requestedAtUtc));
        return this with
        {
            Revision = checked(Revision + 1),
            State = new PendingResourceDeploymentState(
                DesiredGeneration,
                DesiredOperation,
                AttemptCount: 0,
                normalizedRequestedAtUtc),
            UpdatedAtUtc = normalizedRequestedAtUtc
        };
    }

    public ResourceDeployment Retry(DateTime requestedAtUtc)
    {
        if (State is not FailedResourceDeploymentState failed ||
            failed.Generation != DesiredGeneration ||
            failed.Operation != DesiredOperation ||
            !failed.Retryable)
        {
            throw new InvalidOperationException("Only the current failed deployment operation can be retried.");
        }

        var normalizedRequestedAtUtc = ResourceDeploymentValueGuard.UpdatedUtc(
            requestedAtUtc,
            UpdatedAtUtc,
            nameof(requestedAtUtc));
        return this with
        {
            Revision = checked(Revision + 1),
            State = new PendingResourceDeploymentState(
                DesiredGeneration,
                DesiredOperation,
                failed.AttemptCount,
                normalizedRequestedAtUtc),
            UpdatedAtUtc = normalizedRequestedAtUtc
        };
    }

    public ResourceDeployment Start(
        long expectedGeneration,
        ResourceDeploymentOperation operation,
        string claimId,
        DateTime startedAtUtc,
        DateTime leaseExpiresAtUtc)
    {
        var pending = State as PendingResourceDeploymentState ??
            throw new InvalidOperationException("Only a pending deployment operation can be claimed.");
        EnsureCurrentOperation(expectedGeneration, operation);
        if (pending.Generation != expectedGeneration || pending.Operation != operation)
            throw new InvalidOperationException("The pending deployment operation changed before it was claimed.");

        var normalizedClaimId = ResourceDeploymentValueGuard.ObjectId(claimId, nameof(claimId));
        var normalizedStartedAtUtc = ResourceDeploymentValueGuard.UpdatedUtc(
            startedAtUtc,
            UpdatedAtUtc,
            nameof(startedAtUtc));
        var normalizedLeaseExpiresAtUtc = ResourceDeploymentValueGuard.Utc(
            leaseExpiresAtUtc,
            nameof(leaseExpiresAtUtc));
        if (normalizedLeaseExpiresAtUtc <= normalizedStartedAtUtc)
            throw new ArgumentException("The reconciliation lease must expire after it starts.", nameof(leaseExpiresAtUtc));

        return this with
        {
            Revision = checked(Revision + 1),
            State = new ReconcilingResourceDeploymentState(
                expectedGeneration,
                operation,
                normalizedClaimId,
                checked(pending.AttemptCount + 1),
                normalizedStartedAtUtc,
                normalizedLeaseExpiresAtUtc),
            UpdatedAtUtc = normalizedStartedAtUtc
        };
    }

    public ResourceDeployment Complete(
        long expectedGeneration,
        ResourceDeploymentOperation operation,
        string claimId,
        IEnumerable<ResourceDeploymentEndpoint> endpoints,
        DateTime completedAtUtc)
    {
        var reconciling = CurrentClaim(expectedGeneration, operation, claimId);
        var normalizedCompletedAtUtc = ResourceDeploymentValueGuard.UpdatedUtc(
            completedAtUtc,
            UpdatedAtUtc,
            nameof(completedAtUtc));
        if (normalizedCompletedAtUtc < reconciling.StartedAtUtc)
            throw new ArgumentException("The operation cannot complete before it starts.", nameof(completedAtUtc));

        var normalizedEndpoints = operation == ResourceDeploymentOperation.Apply
            ? ResourceDeploymentEndpoint.Normalize(endpoints)
            : ImmutableList<ResourceDeploymentEndpoint>.Empty;
        var hasNewerIntent =
            DesiredGeneration != expectedGeneration ||
            DesiredOperation != operation;
        ResourceDeploymentState nextState = hasNewerIntent
            ? new PendingResourceDeploymentState(
                DesiredGeneration,
                DesiredOperation,
                AttemptCount: 0,
                normalizedCompletedAtUtc)
            : operation == ResourceDeploymentOperation.Apply
                ? new AppliedResourceDeploymentState(
                    expectedGeneration,
                    normalizedCompletedAtUtc)
                : new DeletedResourceDeploymentState(
                    expectedGeneration,
                    normalizedCompletedAtUtc);

        return this with
        {
            Revision = checked(Revision + 1),
            ObservedGeneration = Math.Max(ObservedGeneration, expectedGeneration),
            State = nextState,
            Endpoints = operation == ResourceDeploymentOperation.Apply
                ? normalizedEndpoints
                : ImmutableList<ResourceDeploymentEndpoint>.Empty,
            UpdatedAtUtc = normalizedCompletedAtUtc
        };
    }

    public ResourceDeployment Fail(
        long expectedGeneration,
        ResourceDeploymentOperation operation,
        string claimId,
        string failureCode,
        string failureMessage,
        bool retryable,
        DateTime failedAtUtc,
        DateTime retryNotBeforeUtc)
    {
        var reconciling = CurrentClaim(expectedGeneration, operation, claimId);
        var normalizedFailedAtUtc = ResourceDeploymentValueGuard.UpdatedUtc(
            failedAtUtc,
            UpdatedAtUtc,
            nameof(failedAtUtc));
        if (normalizedFailedAtUtc < reconciling.StartedAtUtc)
            throw new ArgumentException("The operation cannot fail before it starts.", nameof(failedAtUtc));

        var normalizedRetryAtUtc = ResourceDeploymentValueGuard.Utc(
            retryNotBeforeUtc,
            nameof(retryNotBeforeUtc));
        if (normalizedRetryAtUtc < normalizedFailedAtUtc)
            throw new ArgumentException("The retry time cannot be before the failed attempt.", nameof(retryNotBeforeUtc));
        var hasNewerIntent =
            DesiredGeneration != expectedGeneration ||
            DesiredOperation != operation;
        return this with
        {
            Revision = checked(Revision + 1),
            State = hasNewerIntent
                ? new PendingResourceDeploymentState(
                    DesiredGeneration,
                    DesiredOperation,
                    AttemptCount: 0,
                    normalizedFailedAtUtc)
                : new FailedResourceDeploymentState(
                    expectedGeneration,
                    operation,
                    ResourceDeploymentValueGuard.Required(
                        failureCode,
                        nameof(failureCode),
                        ResourceDeploymentValueGuard.MaximumFailureCodeLength),
                    ResourceDeploymentValueGuard.Required(
                        failureMessage,
                        nameof(failureMessage),
                        ResourceDeploymentValueGuard.MaximumFailureMessageLength),
                    retryable,
                    reconciling.AttemptCount,
                    normalizedFailedAtUtc,
                    normalizedRetryAtUtc),
            UpdatedAtUtc = normalizedFailedAtUtc
        };
    }

    public ResourceDeployment RecoverStaleClaim(DateTime requestedAtUtc)
    {
        if (State is not ReconcilingResourceDeploymentState)
            throw new InvalidOperationException("Only a claimed deployment operation can be recovered.");

        var normalizedRequestedAtUtc = ResourceDeploymentValueGuard.UpdatedUtc(
            requestedAtUtc,
            UpdatedAtUtc,
            nameof(requestedAtUtc));
        return this with
        {
            Revision = checked(Revision + 1),
            State = new PendingResourceDeploymentState(
                DesiredGeneration,
                DesiredOperation,
                ((ReconcilingResourceDeploymentState)State).AttemptCount,
                normalizedRequestedAtUtc),
            UpdatedAtUtc = normalizedRequestedAtUtc
        };
    }

    private ReconcilingResourceDeploymentState CurrentClaim(
        long expectedGeneration,
        ResourceDeploymentOperation operation,
        string claimId)
    {
        EnsureCurrentOperation(expectedGeneration, operation, allowNewerIntent: true);
        var normalizedClaimId = ResourceDeploymentValueGuard.ObjectId(claimId, nameof(claimId));
        if (State is not ReconcilingResourceDeploymentState reconciling ||
            reconciling.Generation != expectedGeneration ||
            reconciling.Operation != operation ||
            !string.Equals(reconciling.ClaimId, normalizedClaimId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The deployment claim changed while the operation was in progress.");
        }

        return reconciling;
    }

    private void EnsureCurrentOperation(
        long expectedGeneration,
        ResourceDeploymentOperation operation,
        bool allowNewerIntent = false)
    {
        if (expectedGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(expectedGeneration), "Generation must be positive.");
        if (!allowNewerIntent &&
            (DesiredGeneration != expectedGeneration || DesiredOperation != operation))
        {
            throw new InvalidOperationException("The desired deployment operation changed.");
        }
    }

    private void EnsureSamePlacement(ResolvedResourcePlacement placement)
    {
        if (!string.Equals(RuntimeCellId, placement.RuntimeCellId, StringComparison.Ordinal) ||
            !string.Equals(
                ResolvedPlacement.DeploymentTargetId,
                placement.DeploymentTargetId,
                StringComparison.Ordinal) ||
            !string.Equals(
                ResolvedPlacement.KubernetesNamespace,
                placement.KubernetesNamespace,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Resource placement cannot be moved to another runtime cell, target, or namespace.");
        }
    }

    private void EnsureNotDeleted()
    {
        if (DesiredOperation == ResourceDeploymentOperation.Delete ||
            State is DeletedResourceDeploymentState)
        {
            throw new InvalidOperationException("A deployment pending deletion cannot be reconciled.");
        }
    }
}

public enum ResourceDeploymentOperation
{
    Apply,
    Delete
}

public static class ResourceDeploymentStateKinds
{
    public const string Prepared = "prepared";
    public const string WaitingForRuntimeCell = "waiting-for-runtime-cell";
    public const string Pending = "pending";
    public const string Reconciling = "reconciling";
    public const string Applied = "applied";
    public const string Failed = "failed";
    public const string Deleted = "deleted";
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(PreparedResourceDeploymentState),
    typeof(WaitingForRuntimeCellResourceDeploymentState),
    typeof(PendingResourceDeploymentState),
    typeof(ReconcilingResourceDeploymentState),
    typeof(AppliedResourceDeploymentState),
    typeof(FailedResourceDeploymentState),
    typeof(DeletedResourceDeploymentState))]
public abstract record ResourceDeploymentState(long Generation);

[BsonDiscriminator(ResourceDeploymentStateKinds.Prepared)]
public sealed record PreparedResourceDeploymentState(long Generation) : ResourceDeploymentState(Generation);

[BsonDiscriminator(ResourceDeploymentStateKinds.WaitingForRuntimeCell)]
public sealed record WaitingForRuntimeCellResourceDeploymentState(
    long Generation,
    DateTime RequestedAtUtc)
    : ResourceDeploymentState(Generation);

[BsonDiscriminator(ResourceDeploymentStateKinds.Pending)]
public sealed record PendingResourceDeploymentState(
    long Generation,
    ResourceDeploymentOperation Operation,
    int AttemptCount,
    DateTime RequestedAtUtc)
    : ResourceDeploymentState(Generation);

[BsonDiscriminator(ResourceDeploymentStateKinds.Reconciling)]
public sealed record ReconcilingResourceDeploymentState(
    long Generation,
    ResourceDeploymentOperation Operation,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string ClaimId,
    int AttemptCount,
    DateTime StartedAtUtc,
    DateTime LeaseExpiresAtUtc)
    : ResourceDeploymentState(Generation);

[BsonDiscriminator(ResourceDeploymentStateKinds.Applied)]
public sealed record AppliedResourceDeploymentState(
    long Generation,
    DateTime AppliedAtUtc)
    : ResourceDeploymentState(Generation);

[BsonDiscriminator(ResourceDeploymentStateKinds.Failed)]
public sealed record FailedResourceDeploymentState(
    long Generation,
    ResourceDeploymentOperation Operation,
    string FailureCode,
    string FailureMessage,
    bool Retryable,
    int AttemptCount,
    DateTime FailedAtUtc,
    DateTime RetryNotBeforeUtc)
    : ResourceDeploymentState(Generation);

[BsonDiscriminator(ResourceDeploymentStateKinds.Deleted)]
public sealed record DeletedResourceDeploymentState(
    long Generation,
    DateTime DeletedAtUtc)
    : ResourceDeploymentState(Generation);

public sealed record ResolvedResourcePlacement(
    [property: BsonRepresentation(BsonType.ObjectId)]
    string RuntimeCellId,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string DeploymentTargetId,
    string KubernetesNamespace,
    long RuntimeCellGeneration,
    long TargetWorkloadConfigurationRevision,
    ResolvedIngressConfiguration? Ingress,
    ResolvedStorageConfiguration? Storage,
    ResolvedComputeConfiguration Compute,
    DateTime ResolvedAtUtc)
{
    public static ResolvedResourcePlacement Create(
        string runtimeCellId,
        string deploymentTargetId,
        string kubernetesNamespace,
        long runtimeCellGeneration,
        long targetWorkloadConfigurationRevision,
        ResolvedIngressConfiguration? ingress,
        ResolvedStorageConfiguration? storage,
        ResolvedComputeConfiguration compute,
        DateTime resolvedAtUtc) =>
        new(
            ResourceDeploymentValueGuard.ObjectId(runtimeCellId, nameof(runtimeCellId)),
            ResourceDeploymentValueGuard.ObjectId(deploymentTargetId, nameof(deploymentTargetId)),
            ResourceDeploymentValueGuard.KubernetesNamespace(
                kubernetesNamespace,
                nameof(kubernetesNamespace)),
            ResourceDeploymentValueGuard.Positive(
                runtimeCellGeneration,
                nameof(runtimeCellGeneration)),
            ResourceDeploymentValueGuard.PositiveOrZero(
                targetWorkloadConfigurationRevision,
                nameof(targetWorkloadConfigurationRevision)),
            ingress,
            storage,
            compute ?? throw new ArgumentNullException(nameof(compute)),
            ResourceDeploymentValueGuard.Utc(resolvedAtUtc, nameof(resolvedAtUtc)));
}

public sealed record ResolvedIngressConfiguration(
    string IngressClassName,
    string ClusterIssuerName,
    string? PublicBaseDomain,
    ImmutableList<string> Hostnames)
{
    public static ResolvedIngressConfiguration Create(
        string ingressClassName,
        string clusterIssuerName) =>
        Create(ingressClassName, clusterIssuerName, null, []);

    public static ResolvedIngressConfiguration Create(
        string ingressClassName,
        string clusterIssuerName,
        string? publicBaseDomain,
        IEnumerable<string>? hostnames) =>
        new(
            ResourceDeploymentValueGuard.Required(
                ingressClassName,
                nameof(ingressClassName),
                ResourceDeploymentValueGuard.MaximumKubernetesNameLength),
            ResourceDeploymentValueGuard.Required(
                clusterIssuerName,
                nameof(clusterIssuerName),
                ResourceDeploymentValueGuard.MaximumKubernetesNameLength),
            publicBaseDomain is null
                ? null
                : DnsName.Create(publicBaseDomain).Value,
            NormalizeHostnames(publicBaseDomain, hostnames));

    private static ImmutableList<string> NormalizeHostnames(
        string? publicBaseDomain,
        IEnumerable<string>? hostnames)
    {
        var baseDomain = publicBaseDomain is null
            ? null
            : DnsName.Create(publicBaseDomain);
        var normalized = (hostnames ?? [])
            .Select(hostname => DnsName.Create(hostname))
            .Select(hostname =>
            {
                if (baseDomain is not null && !hostname.IsSameOrDescendantOf(baseDomain))
                {
                    throw new ArgumentException(
                        "Ingress hostnames must be under the public base domain.",
                        nameof(hostnames));
                }

                return hostname.Value;
            })
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableList();

        return normalized;
    }
}

public sealed record ResolvedStorageConfiguration(
    [property: BsonRepresentation(BsonType.ObjectId)]
    string StorageProfileId,
    long StorageProfileRevision,
    string StorageClassName)
{
    public static ResolvedStorageConfiguration Create(
        string storageProfileId,
        long storageProfileRevision,
        string storageClassName) =>
        new(
            ResourceDeploymentValueGuard.ObjectId(
                storageProfileId,
                nameof(storageProfileId)),
            ResourceDeploymentValueGuard.Positive(
                storageProfileRevision,
                nameof(storageProfileRevision)),
            ResourceDeploymentValueGuard.Required(
                storageClassName,
                nameof(storageClassName),
                ResourceDeploymentValueGuard.MaximumKubernetesNameLength));
}

public sealed record ResourceDeploymentEndpoint(
    string Kind,
    string Host,
    int Port,
    string Scheme)
{
    public static ResourceDeploymentEndpoint Create(
        string kind,
        string host,
        int port,
        string scheme) =>
        new(
            ResourceDeploymentValueGuard.Required(
                kind,
                nameof(kind),
                ResourceDeploymentValueGuard.MaximumEndpointKindLength),
            NormalizeHost(host),
            NormalizePort(port),
            ResourceDeploymentValueGuard.Scheme(scheme, nameof(scheme)));

    internal static ImmutableList<ResourceDeploymentEndpoint> Normalize(
        IEnumerable<ResourceDeploymentEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints
            .Select(endpoint => endpoint ?? throw new ArgumentException(
                "Deployment endpoints cannot contain null values.",
                nameof(endpoints)))
            .Distinct()
            .OrderBy(endpoint => endpoint.Kind, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.Host, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.Port)
            .ToImmutableList();
    }

    private static string NormalizeHost(string host) =>
        ResourceDeploymentValueGuard.Required(
            host,
            nameof(host),
            ResourceDeploymentValueGuard.MaximumEndpointHostLength);

    private static int NormalizePort(int port) =>
        port is >= 1 and <= 65535
            ? port
            : throw new ArgumentOutOfRangeException(nameof(port), "Endpoint port is invalid.");
}

public static class ResourceDeploymentFactory
{
    public sealed record CreateOptions(
    string Id,
    DateTime CreatedAtUtc,
    long ResourceRevision,
    int FixedReplicas,
    bool DeferredActivation);

    public static CreateOptions DefaultOptions => new(
        ObjectId.GenerateNewId().ToString(),
        DateTime.UtcNow,
        ResourceRevision: 1,
        FixedReplicas: 1,
        DeferredActivation: false);

    public static ResourceDeployment Create(
        string tenantId,
        ResourceTypeId resourceType,
        string resourceId,
        string resourceGroupId,
        string isolationScopeId,
        ResolvedResourcePlacement resolvedPlacement,
        ResourcePlacementCapability placementCapability,
        bool runtimeCellReady,
        CreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(resolvedPlacement);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(resourceType.Value))
            throw new ArgumentException("Resource type is required.", nameof(resourceType));

        var id = ResourceDeploymentValueGuard.ObjectId(options.Id, nameof(options.Id));
        var normalizedTenantId = ResourceDeploymentValueGuard.ObjectId(
            tenantId,
            nameof(tenantId));
        var normalizedResourceId = ResourceDeploymentValueGuard.ObjectId(
            resourceId,
            nameof(resourceId));
        var normalizedGroupId = ResourceDeploymentValueGuard.ObjectId(
            resourceGroupId,
            nameof(resourceGroupId));
        var normalizedScopeId = ResourceDeploymentValueGuard.ObjectId(
            isolationScopeId,
            nameof(isolationScopeId));
        var createdAtUtc = ResourceDeploymentValueGuard.Utc(
            options.CreatedAtUtc,
            nameof(options.CreatedAtUtc));
        var resourceRevision = ResourceDeploymentValueGuard.Positive(
            options.ResourceRevision,
            nameof(options.ResourceRevision));
        var fixedReplicas = ResourceDeploymentValueGuard.PositiveInt(
            options.FixedReplicas,
            nameof(options.FixedReplicas));
        const long initialGeneration = 1;
        var placementKey = placementCapability switch
        {
            ResourcePlacementCapability.SingleTarget =>
                $"{resourceType.Value}:{normalizedResourceId}:single",
            ResourcePlacementCapability.MultiTarget =>
                $"{resourceType.Value}:{normalizedResourceId}:cell:{resolvedPlacement.RuntimeCellId}",
            _ => throw new ArgumentOutOfRangeException(
                nameof(placementCapability),
                placementCapability,
                "Unsupported placement capability.")
        };

        return new ResourceDeployment(
            id,
            normalizedTenantId,
            resourceType.Value,
            normalizedResourceId,
            normalizedGroupId,
            normalizedScopeId,
            resolvedPlacement.RuntimeCellId,
            placementKey,
            placementCapability,
            resolvedPlacement,
            resourceRevision,
            fixedReplicas,
            Revision: 1,
            DesiredGeneration: initialGeneration,
            ResourceDeploymentOperation.Apply,
            ObservedGeneration: 0,
            options.DeferredActivation
                ? new PreparedResourceDeploymentState(initialGeneration)
                : runtimeCellReady
                ? new PendingResourceDeploymentState(
                    initialGeneration,
                    ResourceDeploymentOperation.Apply,
                    AttemptCount: 0,
                    createdAtUtc)
                : new WaitingForRuntimeCellResourceDeploymentState(
                    initialGeneration,
                    createdAtUtc),
            ImmutableList<ResourceDeploymentEndpoint>.Empty,
            createdAtUtc,
            createdAtUtc,
            ReferenceFence: 0L);
    }
}

internal static class ResourceDeploymentValueGuard
{
    public const int MaximumFailureCodeLength = 128;
    public const int MaximumFailureMessageLength = 512;
    public const int MaximumKubernetesNameLength = 253;
    public const int MaximumEndpointKindLength = 64;
    public const int MaximumEndpointHostLength = 253;
    public const int MaximumDisplayNameLength = 80;

    public static string Required(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("The value is required.", parameterName);

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The value must be {maximumLength} characters or less.",
                parameterName);
        }

        return normalized;
    }

    public static string Optional(string? value, string parameterName, int maximumLength)
    {
        if (value is null)
            throw new ArgumentNullException(parameterName);
        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException(
                $"The value must be {maximumLength} characters or less.",
                parameterName);
        }

        return normalized;
    }

    public static string ObjectId(string? value, string parameterName)
    {
        var normalized = Required(value, parameterName, 24);
        if (!MongoDB.Bson.ObjectId.TryParse(normalized, out _))
            throw new ArgumentException("The value must be a MongoDB ObjectId.", parameterName);
        return normalized;
    }

    public static string KubernetesNamespace(string? value, string parameterName)
    {
        var normalized = Required(value, parameterName, 63);
        if (normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '-')) ||
            !char.IsAsciiLetterOrDigit(normalized[0]) ||
            !char.IsAsciiLetterOrDigit(normalized[^1]))
        {
            throw new ArgumentException("The value must be a Kubernetes DNS label.", parameterName);
        }
        return normalized.ToLowerInvariant();
    }

    public static string Scheme(string? value, string parameterName)
    {
        var normalized = Required(value, parameterName, 32).ToLowerInvariant();
        if (normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) ||
                  character is '+' or '-' or '.')) ||
            !char.IsAsciiLetter(normalized[0]))
        {
            throw new ArgumentException("The value must be a URI scheme.", parameterName);
        }
        return normalized;
    }

    public static long Positive(long value, string parameterName) =>
        value > 0
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "The value must be positive.");

    public static long PositiveOrZero(long value, string parameterName) =>
        value >= 0
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, "The value cannot be negative.");

    public static DateTime Utc(DateTime value, string parameterName)
    {
        if (value == default)
            throw new ArgumentException("The timestamp is required.", parameterName);
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    public static int PositiveInt(int value, string parameterName) =>
        value > 0
            ? value
            : throw new ArgumentOutOfRangeException(
                parameterName,
                "The value must be positive.");

    public static DateTime UpdatedUtc(
        DateTime value,
        DateTime currentValue,
        string parameterName)
    {
        var normalized = Utc(value, parameterName);
        if (normalized < currentValue)
            throw new ArgumentException("The update time cannot move backwards.", parameterName);
        return normalized;
    }
}
