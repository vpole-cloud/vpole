// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.ResourceGroups.Domain;

public sealed record RuntimeCell(
    [property: BsonId]
    [property: BsonRepresentation(BsonType.ObjectId)]
    string Id,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string TenantId,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string ResourceGroupId,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string IsolationScopeId,
    [property: BsonRepresentation(BsonType.ObjectId)]
    string DeploymentTargetId,
    string KubernetesNamespace,
    long DesiredGeneration,
    long ObservedGeneration,
    RuntimeCellState State,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
) : ITenantOwned
{
    public RuntimeCell MarkReady(long expectedGeneration, DateTime readyAtUtc)
    {
        var provisioning = CurrentState<ProvisioningRuntimeCellState>(expectedGeneration);
        var normalizedReadyAtUtc = ResourceGroupValueGuard.UpdatedUtc(readyAtUtc, UpdatedAtUtc, nameof(readyAtUtc));
        if (normalizedReadyAtUtc < provisioning.RequestedAtUtc)
            throw new ArgumentException(
                "A runtime cell cannot become ready before provisioning was requested.",
                nameof(readyAtUtc));

        return this with
        {
            ObservedGeneration = expectedGeneration,
            State = new ReadyRuntimeCellState(expectedGeneration, normalizedReadyAtUtc),
            UpdatedAtUtc = normalizedReadyAtUtc,
        };
    }

    public RuntimeCell MarkFailed(
        long expectedGeneration,
        RuntimeCellOperation operation,
        string failureCode,
        string failureMessage,
        DateTime attemptedAtUtc,
        DateTime retryNotBeforeUtc)
    {
        var activeState = CurrentState(expectedGeneration);
        if (!MatchesOperation(activeState, operation))
            throw new InvalidOperationException(
                $"Cannot record a {operation} failure from runtime cell state '{activeState.GetType().Name}'.");

        var normalizedAttemptedAtUtc = ResourceGroupValueGuard.UpdatedUtc(
            attemptedAtUtc,
            UpdatedAtUtc,
            nameof(attemptedAtUtc));
        var normalizedRetryNotBeforeUtc = ResourceGroupValueGuard.Utc(retryNotBeforeUtc, nameof(retryNotBeforeUtc));
        if (normalizedRetryNotBeforeUtc < normalizedAttemptedAtUtc)
            throw new ArgumentException(
                "The retry time cannot be before the failed attempt.",
                nameof(retryNotBeforeUtc));

        return this with
        {
            State = new FailedRuntimeCellState(
                expectedGeneration,
                operation,
                ResourceGroupValueGuard.Required(
                    failureCode,
                    nameof(failureCode),
                    ResourceGroupValueGuard.MaximumRuntimeCellFailureCodeLength),
                ResourceGroupValueGuard.Required(
                    failureMessage,
                    nameof(failureMessage),
                    ResourceGroupValueGuard.MaximumRuntimeCellFailureMessageLength),
                normalizedAttemptedAtUtc,
                normalizedRetryNotBeforeUtc),
            UpdatedAtUtc = normalizedAttemptedAtUtc,
        };
    }

    public RuntimeCell RequestReconciliation(DateTime requestedAtUtc)
    {
        if (State is DeletingRuntimeCellState or DeletedRuntimeCellState || State is FailedRuntimeCellState
            {
                Operation: RuntimeCellOperation.Deletion,
            })
            throw new InvalidOperationException("A runtime cell pending deletion cannot be reconciled.");

        var normalizedRequestedAtUtc = ResourceGroupValueGuard.UpdatedUtc(
            requestedAtUtc,
            UpdatedAtUtc,
            nameof(requestedAtUtc));
        var nextGeneration = checked(DesiredGeneration + 1);
        return this with
        {
            DesiredGeneration = nextGeneration,
            State = new ProvisioningRuntimeCellState(nextGeneration, normalizedRequestedAtUtc),
            UpdatedAtUtc = normalizedRequestedAtUtc,
        };
    }

    public RuntimeCell RequestDeletion(DateTime requestedAtUtc)
    {
        if (State is DeletedRuntimeCellState or DeletingRuntimeCellState)
            return this;

        var normalizedRequestedAtUtc = ResourceGroupValueGuard.UpdatedUtc(
            requestedAtUtc,
            UpdatedAtUtc,
            nameof(requestedAtUtc));
        var nextGeneration = checked(DesiredGeneration + 1);
        return this with
        {
            DesiredGeneration = nextGeneration,
            State = new DeletingRuntimeCellState(nextGeneration, normalizedRequestedAtUtc, null, 0),
            UpdatedAtUtc = normalizedRequestedAtUtc,
        };
    }

    public RuntimeCell ScheduleDeletionCheck(
        long expectedGeneration,
        DateTime checkedAtUtc,
        DateTime nextCheckAtUtc)
    {
        var deleting = CurrentState<DeletingRuntimeCellState>(expectedGeneration);
        var checkedAt = ResourceGroupValueGuard.UpdatedUtc(checkedAtUtc, UpdatedAtUtc, nameof(checkedAtUtc));
        var due = ResourceGroupValueGuard.Utc(nextCheckAtUtc, nameof(nextCheckAtUtc));
        if (due <= checkedAt)
            throw new ArgumentException(
                "A deletion check must be scheduled after the current check.",
                nameof(nextCheckAtUtc));

        return this with
        {
            State = deleting with
            {
                NextCheckAtUtc = due,
                CheckSequence = checked(deleting.CheckSequence + 1),
            },
            UpdatedAtUtc = checkedAt,
        };
    }

    public RuntimeCell MarkDeleted(long expectedGeneration, DateTime deletedAtUtc)
    {
        var deleting = CurrentState<DeletingRuntimeCellState>(expectedGeneration);
        var normalizedDeletedAtUtc = ResourceGroupValueGuard.UpdatedUtc(
            deletedAtUtc,
            UpdatedAtUtc,
            nameof(deletedAtUtc));
        if (normalizedDeletedAtUtc < deleting.RequestedAtUtc)
            throw new ArgumentException(
                "A runtime cell cannot be deleted before deletion was requested.",
                nameof(deletedAtUtc));

        return this with
        {
            ObservedGeneration = expectedGeneration,
            State = new DeletedRuntimeCellState(expectedGeneration, normalizedDeletedAtUtc),
            UpdatedAtUtc = normalizedDeletedAtUtc,
        };
    }

    private TState CurrentState<TState>(long expectedGeneration)
        where TState : RuntimeCellState
    {
        var state = CurrentState(expectedGeneration);
        if (state is not TState typedState)
            throw new InvalidOperationException(
                $"Cannot apply the requested transition from runtime cell state '{state.GetType().Name}'.");

        return typedState;
    }

    private RuntimeCellState CurrentState(long expectedGeneration)
    {
        if (expectedGeneration < 1)
            throw new ArgumentOutOfRangeException(
                nameof(expectedGeneration),
                "The expected generation must be positive.");
        if (DesiredGeneration != expectedGeneration || State.Generation != expectedGeneration)
            throw new InvalidOperationException("The runtime cell changed while the operation was in progress.");

        return State;
    }

    private static bool MatchesOperation(RuntimeCellState state, RuntimeCellOperation operation) =>
        (state, operation) switch
        {
            (ProvisioningRuntimeCellState, RuntimeCellOperation.Reconciliation) => true,
            (DeletingRuntimeCellState, RuntimeCellOperation.Deletion) => true,
            _ => false,
        };
}

public enum RuntimeCellOperation
{
    Reconciliation,
    Deletion,
}

[BsonDiscriminator(RootClass = true)]
[BsonKnownTypes(
    typeof(ProvisioningRuntimeCellState),
    typeof(ReadyRuntimeCellState),
    typeof(FailedRuntimeCellState),
    typeof(DeletingRuntimeCellState),
    typeof(DeletedRuntimeCellState))]
public abstract record RuntimeCellState(
    long Generation
);

[BsonDiscriminator("provisioning")]
public sealed record ProvisioningRuntimeCellState(
    long Generation,
    DateTime RequestedAtUtc
) : RuntimeCellState(Generation);

[BsonDiscriminator("ready")]
public sealed record ReadyRuntimeCellState(
    long Generation,
    DateTime ReadyAtUtc
) : RuntimeCellState(Generation);

[BsonDiscriminator("failed")]
public sealed record FailedRuntimeCellState(
    long Generation,
    RuntimeCellOperation Operation,
    string FailureCode,
    string FailureMessage,
    DateTime AttemptedAtUtc,
    DateTime RetryNotBeforeUtc
) : RuntimeCellState(Generation);

[BsonDiscriminator("deleting")]
public sealed record DeletingRuntimeCellState(
    long Generation,
    DateTime RequestedAtUtc,
    DateTime? NextCheckAtUtc,
    long CheckSequence
) : RuntimeCellState(Generation);

[BsonDiscriminator("deleted")]
public sealed record DeletedRuntimeCellState(
    long Generation,
    DateTime DeletedAtUtc
) : RuntimeCellState(Generation);

public static class RuntimeCellFactory
{
    public static CreateOptions DefaultOptions => new(ObjectId.GenerateNewId().ToString(), DateTime.UtcNow);

    public static RuntimeCell Create(
        string tenantId,
        string resourceGroupId,
        string isolationScopeId,
        string deploymentTargetId,
        string kubernetesNamespace,
        CreateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var createdAtUtc = ResourceGroupValueGuard.Utc(options.CreatedAtUtc, nameof(options.CreatedAtUtc));
        const long initialGeneration = 1;
        return new RuntimeCell(
            ResourceGroupValueGuard.ObjectId(options.Id, nameof(options.Id)),
            ResourceGroupValueGuard.ObjectId(tenantId, nameof(tenantId)),
            ResourceGroupValueGuard.ObjectId(resourceGroupId, nameof(resourceGroupId)),
            ResourceGroupValueGuard.ObjectId(isolationScopeId, nameof(isolationScopeId)),
            ResourceGroupValueGuard.ObjectId(deploymentTargetId, nameof(deploymentTargetId)),
            ResourceGroupValueGuard.KubernetesNamespace(kubernetesNamespace, nameof(kubernetesNamespace)),
            DesiredGeneration: initialGeneration,
            ObservedGeneration: 0,
            new ProvisioningRuntimeCellState(initialGeneration, createdAtUtc),
            createdAtUtc,
            createdAtUtc);
    }

    public sealed record CreateOptions(
        string Id,
        DateTime CreatedAtUtc
    );
}
