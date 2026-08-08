using System.Collections.Immutable;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.ResourceDeployments.Contracts;

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
    long Revision,
    long DesiredGeneration,
    ResourceDeploymentOperation DesiredOperation,
    long ObservedGeneration,
    ResourceDeploymentState State,
    ImmutableList<ResourceDeploymentEndpoint> Endpoints,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc)
{
    public ResourceDeployment RequestReconciliation(
        ResolvedResourcePlacement resolvedPlacement,
        bool runtimeCellReady,
        DateTime requestedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(resolvedPlacement);
        EnsureSamePlacement(resolvedPlacement);
        EnsureNotDeleted();

        var normalizedRequestedAtUtc = ResourceDeploymentValueGuard.UpdatedUtc(
            requestedAtUtc,
            UpdatedAtUtc,
            nameof(requestedAtUtc));
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
            Revision = checked(Revision + 1),
            DesiredGeneration = nextGeneration,
            DesiredOperation = ResourceDeploymentOperation.Apply,
            State = nextState,
            UpdatedAtUtc = normalizedRequestedAtUtc
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

    public ResourceDeployment RequestDeletion(DateTime requestedAtUtc)
    {
        if (State is DeletedResourceDeploymentState)
            return this;
        if (DesiredOperation == ResourceDeploymentOperation.Delete &&
            State is not FailedResourceDeploymentState)
        {
            return this;
        }

        var normalizedRequestedAtUtc = ResourceDeploymentValueGuard.UpdatedUtc(
            requestedAtUtc,
            UpdatedAtUtc,
            nameof(requestedAtUtc));
        var nextGeneration = checked(DesiredGeneration + 1);
        return this with
        {
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
    public const string WaitingForRuntimeCell = "waiting-for-runtime-cell";
    public const string Pending = "pending";
    public const string Reconciling = "reconciling";
    public const string Applied = "applied";
    public const string Failed = "failed";
    public const string Deleted = "deleted";
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(WaitingForRuntimeCellResourceDeploymentState),
    typeof(PendingResourceDeploymentState),
    typeof(ReconcilingResourceDeploymentState),
    typeof(AppliedResourceDeploymentState),
    typeof(FailedResourceDeploymentState),
    typeof(DeletedResourceDeploymentState))]
public abstract record ResourceDeploymentState(long Generation);

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

public enum ResolvedComputeArchitecture
{
    Any,
    Amd64,
    Arm64
}

public enum ResolvedComputeNodeAffinityOperator
{
    In,
    NotIn,
    Exists,
    DoesNotExist,
    GreaterThan,
    LessThan
}

public enum ResolvedComputeTolerationOperator
{
    Equal,
    Exists
}

public enum ResolvedComputeTaintEffect
{
    Any,
    NoSchedule,
    PreferNoSchedule,
    NoExecute
}

public sealed record ResolvedComputeResourceValues(
    long CpuMillis,
    long MemoryMi)
{
    public static ResolvedComputeResourceValues Create(
        long cpuMillis,
        long memoryMi) =>
        new(
            ResourceDeploymentValueGuard.Positive(cpuMillis, nameof(cpuMillis)),
            ResourceDeploymentValueGuard.Positive(memoryMi, nameof(memoryMi)));
}

public sealed record ResolvedComputeExtendedResource(
    string Name,
    long Quantity)
{
    public static ResolvedComputeExtendedResource Create(
        string name,
        long quantity) =>
        new(
            ResourceDeploymentValueGuard.ExtendedResourceName(
                name,
                nameof(name)),
            ResourceDeploymentValueGuard.Positive(quantity, nameof(quantity)));
}

public sealed record ResolvedComputeNodeSelector(
    string Key,
    string Value)
{
    public static ResolvedComputeNodeSelector Create(
        string key,
        string value) =>
        new(
            ResourceDeploymentValueGuard.LabelKey(
                key,
                nameof(key)),
            ResourceDeploymentValueGuard.LabelValue(
                value,
                nameof(value)));
}

public sealed record ResolvedComputeNodeAffinityExpression(
    string Key,
    ResolvedComputeNodeAffinityOperator Operator,
    ImmutableList<string> Values)
{
    public static ResolvedComputeNodeAffinityExpression Create(
        string key,
        ResolvedComputeNodeAffinityOperator @operator,
        IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!Enum.IsDefined(@operator))
            throw new ArgumentOutOfRangeException(nameof(@operator));

        var normalizedValues = values
            .Select(value => ResourceDeploymentValueGuard.LabelValue(
                value,
                nameof(values)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableList();
        var expectedValueCount = @operator switch
        {
            ResolvedComputeNodeAffinityOperator.In or
            ResolvedComputeNodeAffinityOperator.NotIn => normalizedValues.Count > 0,
            ResolvedComputeNodeAffinityOperator.Exists or
            ResolvedComputeNodeAffinityOperator.DoesNotExist => normalizedValues.Count == 0,
            ResolvedComputeNodeAffinityOperator.GreaterThan or
            ResolvedComputeNodeAffinityOperator.LessThan =>
                normalizedValues.Count == 1 &&
                long.TryParse(normalizedValues[0], out _),
            _ => false
        };
        if (!expectedValueCount)
        {
            throw new ArgumentException(
                "Node-affinity values do not match the selected operator.",
                nameof(values));
        }

        return new ResolvedComputeNodeAffinityExpression(
            ResourceDeploymentValueGuard.LabelKey(
                key,
                nameof(key)),
            @operator,
            normalizedValues);
    }
}

public sealed record ResolvedComputeNodeAffinityTerm(
    ImmutableList<ResolvedComputeNodeAffinityExpression> Expressions)
{
    public static ResolvedComputeNodeAffinityTerm Create(
        IEnumerable<ResolvedComputeNodeAffinityExpression> expressions)
    {
        ArgumentNullException.ThrowIfNull(expressions);
        var normalized = expressions
            .Select(expression => expression ?? throw new ArgumentException(
                "Node-affinity expressions cannot contain null values.",
                nameof(expressions)))
            .Select(expression => ResolvedComputeNodeAffinityExpression.Create(
                expression.Key,
                expression.Operator,
                expression.Values))
            .OrderBy(expression => expression.Key, StringComparer.Ordinal)
            .ThenBy(expression => expression.Operator)
            .ThenBy(
                expression => string.Join(',', expression.Values),
                StringComparer.Ordinal)
            .ToImmutableList();
        if (normalized.Count == 0)
            throw new ArgumentException("A node-affinity term must contain an expression.", nameof(expressions));

        return new ResolvedComputeNodeAffinityTerm(normalized);
    }
}

public sealed record ResolvedComputePreferredNodeAffinityTerm(
    int Weight,
    ResolvedComputeNodeAffinityTerm Term)
{
    public static ResolvedComputePreferredNodeAffinityTerm Create(
        int weight,
        ResolvedComputeNodeAffinityTerm term)
    {
        if (weight is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(weight));
        ArgumentNullException.ThrowIfNull(term);

        return new ResolvedComputePreferredNodeAffinityTerm(
            weight,
            ResolvedComputeNodeAffinityTerm.Create(term.Expressions));
    }
}

public sealed record ResolvedComputeToleration(
    string? Key,
    ResolvedComputeTolerationOperator Operator,
    string? Value,
    ResolvedComputeTaintEffect Effect,
    long? TolerationSeconds)
{
    public static ResolvedComputeToleration Create(
        string? key,
        ResolvedComputeTolerationOperator @operator,
        string? value,
        ResolvedComputeTaintEffect effect,
        long? tolerationSeconds)
    {
        if (!Enum.IsDefined(@operator))
            throw new ArgumentOutOfRangeException(nameof(@operator));
        if (!Enum.IsDefined(effect))
            throw new ArgumentOutOfRangeException(nameof(effect));
        if (tolerationSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerationSeconds));

        var normalizedKey = string.IsNullOrWhiteSpace(key)
            ? null
            : ResourceDeploymentValueGuard.LabelKey(key, nameof(key));
        var normalizedValue = value is null
            ? null
            : ResourceDeploymentValueGuard.LabelValue(value, nameof(value));
        if (@operator == ResolvedComputeTolerationOperator.Equal &&
            (normalizedKey is null || normalizedValue is null))
        {
            throw new ArgumentException(
                "Equal tolerations require both key and value.",
                nameof(value));
        }
        if (@operator == ResolvedComputeTolerationOperator.Exists && normalizedValue is not null)
            throw new ArgumentException("Exists tolerations cannot specify a value.", nameof(value));
        if (normalizedKey is null && @operator != ResolvedComputeTolerationOperator.Exists)
            throw new ArgumentException("An empty toleration key requires the Exists operator.", nameof(key));
        if (tolerationSeconds is not null && effect != ResolvedComputeTaintEffect.NoExecute)
        {
            throw new ArgumentException(
                "Toleration seconds are supported only for NoExecute tolerations.",
                nameof(tolerationSeconds));
        }

        return new ResolvedComputeToleration(
            normalizedKey,
            @operator,
            normalizedValue,
            effect,
            tolerationSeconds);
    }
}

public sealed record ResolvedComputeConfiguration(
    [property: BsonRepresentation(BsonType.ObjectId)]
    string ComputeProfileId,
    long ComputeProfileRevision,
    string DisplayName,
    ResolvedComputeResourceValues Requests,
    ResolvedComputeResourceValues Limits,
    ResolvedComputeArchitecture Architecture,
    ImmutableList<ResolvedComputeExtendedResource> ExtendedResources,
    ImmutableList<ResolvedComputeNodeSelector> NodeSelector,
    ImmutableList<ResolvedComputeNodeAffinityTerm> RequiredNodeAffinity,
    ImmutableList<ResolvedComputePreferredNodeAffinityTerm> PreferredNodeAffinity,
    ImmutableList<ResolvedComputeToleration> Tolerations,
    string? RuntimeClassName)
{
    public static ResolvedComputeConfiguration Create(
        string computeProfileId,
        long computeProfileRevision,
        string displayName,
        ResolvedComputeResourceValues requests,
        ResolvedComputeResourceValues limits,
        ResolvedComputeArchitecture architecture,
        IEnumerable<ResolvedComputeExtendedResource>? extendedResources = null,
        IEnumerable<ResolvedComputeNodeSelector>? nodeSelector = null,
        IEnumerable<ResolvedComputeNodeAffinityTerm>? requiredNodeAffinity = null,
        IEnumerable<ResolvedComputePreferredNodeAffinityTerm>? preferredNodeAffinity = null,
        IEnumerable<ResolvedComputeToleration>? tolerations = null,
        string? runtimeClassName = null)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(limits);
        if (!Enum.IsDefined(architecture))
            throw new ArgumentOutOfRangeException(nameof(architecture));
        var normalizedRequests = ResolvedComputeResourceValues.Create(
            requests.CpuMillis,
            requests.MemoryMi);
        var normalizedLimits = ResolvedComputeResourceValues.Create(
            limits.CpuMillis,
            limits.MemoryMi);
        if (normalizedLimits.CpuMillis < normalizedRequests.CpuMillis ||
            normalizedLimits.MemoryMi < normalizedRequests.MemoryMi)
        {
            throw new ArgumentException("Compute limits cannot be lower than requests.", nameof(limits));
        }
        var normalizedExtendedResources = NormalizeExtendedResources(extendedResources);
        var normalizedNodeSelector = NormalizeNodeSelector(nodeSelector);
        EnsureArchitectureMatchesNodeSelector(architecture, normalizedNodeSelector);

        return new ResolvedComputeConfiguration(
            ResourceDeploymentValueGuard.ObjectId(computeProfileId, nameof(computeProfileId)),
            ResourceDeploymentValueGuard.Positive(
                computeProfileRevision,
                nameof(computeProfileRevision)),
            ResourceDeploymentValueGuard.Required(
                displayName,
                nameof(displayName),
                ResourceDeploymentValueGuard.MaximumDisplayNameLength),
            normalizedRequests,
            normalizedLimits,
            architecture,
            normalizedExtendedResources,
            normalizedNodeSelector,
            (requiredNodeAffinity ?? [])
                .Select(term => ResolvedComputeNodeAffinityTerm.Create(term.Expressions))
                .OrderBy(TermSortKey, StringComparer.Ordinal)
                .ToImmutableList(),
            (preferredNodeAffinity ?? [])
                .Select(term => ResolvedComputePreferredNodeAffinityTerm.Create(
                    term.Weight,
                    term.Term))
                .OrderByDescending(term => term.Weight)
                .ThenBy(term => TermSortKey(term.Term), StringComparer.Ordinal)
                .ToImmutableList(),
            (tolerations ?? [])
                .Select(toleration => ResolvedComputeToleration.Create(
                    toleration.Key,
                    toleration.Operator,
                    toleration.Value,
                    toleration.Effect,
                    toleration.TolerationSeconds))
                .OrderBy(TolerationSortKey, StringComparer.Ordinal)
                .ToImmutableList(),
            ResourceDeploymentValueGuard.OptionalDnsSubdomain(
                runtimeClassName,
                nameof(runtimeClassName)));
    }

    public bool Matches(string computeProfileId, long computeProfileRevision) =>
        string.Equals(ComputeProfileId, computeProfileId, StringComparison.Ordinal) &&
        ComputeProfileRevision == computeProfileRevision;

    private static ImmutableList<ResolvedComputeExtendedResource>
        NormalizeExtendedResources(
            IEnumerable<ResolvedComputeExtendedResource>? extendedResources)
    {
        var normalized = (extendedResources ?? [])
                .Select(resource => ResolvedComputeExtendedResource.Create(
                    resource.Name,
                    resource.Quantity))
                .OrderBy(resource => resource.Name, StringComparer.Ordinal)
                .ToImmutableList();
        if (normalized.Select(resource => resource.Name)
            .Distinct(StringComparer.Ordinal).Count() != normalized.Count)
        {
            throw new ArgumentException(
                "Extended resource names must be unique.",
                nameof(extendedResources));
        }

        return normalized;
    }

    private static ImmutableList<ResolvedComputeNodeSelector>
        NormalizeNodeSelector(
            IEnumerable<ResolvedComputeNodeSelector>? nodeSelector)
    {
        var normalized = (nodeSelector ?? [])
                .Select(selector => ResolvedComputeNodeSelector.Create(
                    selector.Key,
                    selector.Value))
                .OrderBy(selector => selector.Key, StringComparer.Ordinal)
                .ThenBy(selector => selector.Value, StringComparer.Ordinal)
                .ToImmutableList();
        if (normalized.Select(selector => selector.Key)
            .Distinct(StringComparer.Ordinal).Count() != normalized.Count)
        {
            throw new ArgumentException(
                "Node selector keys must be unique.",
                nameof(nodeSelector));
        }

        return normalized;
    }

    private static string TermSortKey(ResolvedComputeNodeAffinityTerm term) =>
        string.Join(
            '|',
            term.Expressions.Select(expression =>
                $"{expression.Key}:{expression.Operator}:{string.Join(',', expression.Values)}"));

    private static string TolerationSortKey(ResolvedComputeToleration toleration) =>
        $"{toleration.Key}:{toleration.Operator}:{toleration.Value}:" +
        $"{toleration.Effect}:{toleration.TolerationSeconds}";

    private static void EnsureArchitectureMatchesNodeSelector(
        ResolvedComputeArchitecture architecture,
        ImmutableList<ResolvedComputeNodeSelector> nodeSelector)
    {
        var expected = architecture switch
        {
            ResolvedComputeArchitecture.Any => null,
            ResolvedComputeArchitecture.Amd64 => "amd64",
            ResolvedComputeArchitecture.Arm64 => "arm64",
            _ => throw new ArgumentOutOfRangeException(nameof(architecture))
        };
        var selected = nodeSelector.FirstOrDefault(selector =>
            string.Equals(
                selector.Key,
                "kubernetes.io/arch",
                StringComparison.Ordinal));
        if (expected is not null &&
            selected is not null &&
            !string.Equals(selected.Value, expected, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The compute architecture conflicts with the node selector.",
                nameof(nodeSelector));
        }
    }
}

public sealed record ResolvedIngressConfiguration(
    string IngressClassName,
    string ClusterIssuerName)
{
    public static ResolvedIngressConfiguration Create(
        string ingressClassName,
        string clusterIssuerName) =>
        new(
            ResourceDeploymentValueGuard.Required(
                ingressClassName,
                nameof(ingressClassName),
                ResourceDeploymentValueGuard.MaximumKubernetesNameLength),
            ResourceDeploymentValueGuard.Required(
                clusterIssuerName,
                nameof(clusterIssuerName),
                ResourceDeploymentValueGuard.MaximumKubernetesNameLength));
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

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(IngressResourceDeploymentEndpoint),
    typeof(RedisResourceDeploymentEndpoint),
    typeof(PostgreSqlResourceDeploymentEndpoint))]
public abstract record ResourceDeploymentEndpoint(string Host, int Port, string Scheme)
{
    internal static ImmutableList<ResourceDeploymentEndpoint> Normalize(
        IEnumerable<ResourceDeploymentEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints
            .Select(endpoint => endpoint ?? throw new ArgumentException(
                "Deployment endpoints cannot contain null values.",
                nameof(endpoints)))
            .Distinct()
            .OrderBy(endpoint => endpoint.GetType().Name, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.Host, StringComparer.Ordinal)
            .ThenBy(endpoint => endpoint.Port)
            .ToImmutableList();
    }

    protected static string NormalizeHost(string host) =>
        ResourceDeploymentValueGuard.Required(
            host,
            nameof(host),
            ResourceDeploymentValueGuard.MaximumEndpointHostLength);

    protected static int NormalizePort(int port) =>
        port is >= 1 and <= 65535
            ? port
            : throw new ArgumentOutOfRangeException(nameof(port), "Endpoint port is invalid.");
}

[BsonDiscriminator("ingress")]
public sealed record IngressResourceDeploymentEndpoint(
    string Host,
    int Port,
    string Scheme)
    : ResourceDeploymentEndpoint(Host, Port, Scheme)
{
    public static IngressResourceDeploymentEndpoint Create(
        string host,
        int port,
        string scheme) =>
        new(
            NormalizeHost(host),
            NormalizePort(port),
            ResourceDeploymentValueGuard.Scheme(scheme, nameof(scheme)));
}

[BsonDiscriminator("redis")]
public sealed record RedisResourceDeploymentEndpoint(string Host, int Port)
    : ResourceDeploymentEndpoint(Host, Port, "redis")
{
    public static RedisResourceDeploymentEndpoint Create(string host, int port) =>
        new(NormalizeHost(host), NormalizePort(port));
}

[BsonDiscriminator("postgresql")]
public sealed record PostgreSqlResourceDeploymentEndpoint(string Host, int Port)
    : ResourceDeploymentEndpoint(Host, Port, "postgresql")
{
    public static PostgreSqlResourceDeploymentEndpoint Create(string host, int port) =>
        new(NormalizeHost(host), NormalizePort(port));
}

public static class ResourceDeploymentFactory
{
    public sealed record CreateOptions(string Id, DateTime CreatedAtUtc);

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
            Revision: 1,
            DesiredGeneration: initialGeneration,
            ResourceDeploymentOperation.Apply,
            ObservedGeneration: 0,
            runtimeCellReady
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
            createdAtUtc);
    }
}

internal static partial class ResourceDeploymentValueGuard
{
    public const int MaximumFailureCodeLength = 128;
    public const int MaximumFailureMessageLength = 512;
    public const int MaximumKubernetesNameLength = 253;
    public const int MaximumEndpointHostLength = 253;
    public const int MaximumDisplayNameLength = 80;
    public const int MaximumLabelNameLength = 63;
    public const int MaximumDnsSubdomainLength = 253;
    public const int MaximumQualifiedNameLength =
        MaximumDnsSubdomainLength + MaximumLabelNameLength + 1;

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

    public static string ExtendedResourceName(string? value, string parameterName)
    {
        var normalized = Required(
            value,
            parameterName,
            MaximumQualifiedNameLength);
        var separator = normalized.IndexOf('/');
        if (separator <= 0 ||
            separator != normalized.LastIndexOf('/') ||
            !IsDnsSubdomain(normalized[..separator]) ||
            !IsLabelName(normalized[(separator + 1)..]) ||
            string.Equals(normalized[..separator], "kubernetes.io", StringComparison.Ordinal) ||
            normalized[..separator].EndsWith(".kubernetes.io", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The value must be a non-Kubernetes qualified resource name.",
                parameterName);
        }

        return normalized;
    }

    public static string LabelKey(string? value, string parameterName)
    {
        var normalized = Required(value, parameterName, MaximumQualifiedNameLength);
        var separator = normalized.IndexOf('/');
        var valid = separator < 0
            ? IsLabelName(normalized)
            : separator == normalized.LastIndexOf('/') &&
              IsDnsSubdomain(normalized[..separator]) &&
              IsLabelName(normalized[(separator + 1)..]);
        return valid
            ? normalized
            : throw new ArgumentException(
                "The value must be a Kubernetes label key.",
                parameterName);
    }

    public static string LabelValue(string? value, string parameterName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= MaximumLabelNameLength &&
               (normalized.Length == 0 || IsLabelName(normalized))
            ? normalized
            : throw new ArgumentException(
                "The value must be a Kubernetes label value.",
                parameterName);
    }

    public static string? OptionalDnsSubdomain(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim();
        return IsDnsSubdomain(normalized)
            ? normalized
            : throw new ArgumentException(
                "The value must be a Kubernetes DNS subdomain.",
                parameterName);
    }

    private static bool IsDnsSubdomain(string value) =>
        value.Length is >= 1 and <= MaximumDnsSubdomainLength &&
        value.Split('.').All(label => DnsLabelPattern().IsMatch(label));

    private static bool IsLabelName(string value) =>
        value.Length is >= 1 and <= MaximumLabelNameLength &&
        LabelNamePattern().IsMatch(value);

    [GeneratedRegex("^[a-z0-9](?:[-a-z0-9]*[a-z0-9])?$")]
    private static partial Regex DnsLabelPattern();

    [GeneratedRegex("^[A-Za-z0-9](?:[-_.A-Za-z0-9]*[A-Za-z0-9])?$")]
    private static partial Regex LabelNamePattern();

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
