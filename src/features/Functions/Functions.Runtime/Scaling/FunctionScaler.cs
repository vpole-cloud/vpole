// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Coordination;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Functions.Protocol;

namespace Pole.Features.Functions.Runtime.Scaling;

public sealed class FunctionScalerOptions
{
    public const string SectionName = "Functions:Scaler";

    [Range(1, 60)] public int PollIntervalSeconds { get; set; } = 2;

    [Range(1, 30)] public int AgentStatusTimeoutSeconds { get; set; } = 2;
}

public sealed class FunctionScalerOptionsValidator : IValidateOptions<FunctionScalerOptions>
{
    public ValidateOptionsResult Validate(string? name, FunctionScalerOptions options) =>
        FunctionOptionsValidation.ToValidateOptionsResult(FunctionOptionsValidation.GetDataAnnotationFailures(options));
}

public interface IFunctionScaler
{
    Task RunOnceAsync(CancellationToken cancellationToken);
}

public sealed class FunctionScaler(
    IFunctionScaleTargetSource targetSource,
    IFunctionEndpointRegistry endpointRegistry,
    IFunctionAgentClient agentClient,
    IFunctionScalingCoordinator scalingCoordinator,
    IFunctionResourcePlacementResolver placementResolver,
    IFunctionRevisionKubernetesManager kubernetesManager,
    FunctionScalerOptions options,
    TimeProvider timeProvider,
    ILogger<FunctionScaler> logger
) : IFunctionScaler, IDisposable
{
    private readonly string ownerId = $"function-scaler-{Guid.NewGuid():N}";

    private readonly ConcurrentDictionary<string, RevisionState> states = new(StringComparer.Ordinal);

    public void Dispose()
    {
        states.Clear();
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var targets = await targetSource.ListAsync(cancellationToken);
        var liveKeys = targets.Select(Key).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in states.Keys.Where(key => !liveKeys.Contains(key)))
            states.TryRemove(stale, out _);

        foreach (var target in targets)
            try
            {
                await ReconcileAsync(target, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "[function-scaler-reconcile-failed] Failed to reconcile Function revision {RevisionId}",
                    target.Revision.Id);
            }
    }

    private async Task ReconcileAsync(FunctionScaleTarget target, CancellationToken cancellationToken)
    {
        var targetKey = Key(target);
        var coordinationKey = CoordinationKey(target);
        var state = states.GetOrAdd(targetKey, _ => new RevisionState($"function-scaler-owner:{ownerId}:{targetKey}"));
        var ownership = await EnsureOwnershipAsync(coordinationKey, state, cancellationToken);
        if (ownership is null)
            return;

        var coordination = await scalingCoordinator.GetSnapshotAsync(coordinationKey, cancellationToken);
        if (!state.ObserveCoordinator(coordination.CoordinatorIncarnation) || !string.Equals(
                ownership.CoordinatorIncarnation,
                coordination.CoordinatorIncarnation,
                StringComparison.Ordinal))
            return;

        var resolvedPlacement = await placementResolver.ResolveAsync(target.App, cancellationToken: cancellationToken);
        if (resolvedPlacement is FunctionResourcePlacementUnavailable)
            return;

        var placement = (FunctionResourcePlacement)resolvedPlacement.Value;

        var subject = new FunctionRevisionInKubernetes(target.App, target.Revision, placement);
        var service = new FunctionServiceKey(placement.DeploymentTargetId, subject.Namespace, subject.ServiceName);
        var snapshot = endpointRegistry.GetSnapshot(service);
        var replicasResult = await kubernetesManager.ReadReplicaStateAsync(subject, cancellationToken);
        if (replicasResult is FunctionRevisionKubernetesOperationError)
            return;

        var replicas = (FunctionReplicaState)replicasResult.Value;

        if (await RecoverTransitionAsync(
                target,
                subject,
                service,
                coordinationKey,
                state,
                ownership,
                coordination,
                replicas,
                snapshot.Endpoints.Length,
                cancellationToken))
            return;

        var observation = await ObserveDemandAsync(target, snapshot, coordination.Admissions, state, cancellationToken);
        var readyReplicas = snapshot.Endpoints.Length;

        if (observation.Total > 0)
        {
            state.IdleSinceUtc = null;
            var appDesired = DivideRoundUp(
                observation.Total,
                target.Revision.Spec.Scaling.TargetConcurrencyPerInstance);
            var handlerDesired = target.Revision
                .Spec
                .Handlers
                .Select(handler => observation.HandlerDemand.TryGetValue(handler.Name, out var busy)
                    ? DivideRoundUp(
                        busy,
                        Math.Min(
                            target.Revision.Spec.Scaling.TargetConcurrencyPerInstance,
                            handler.MaxConcurrencyPerInstance))
                    : 0)
                .DefaultIfEmpty(0)
                .Max();
            var desired = Math.Clamp(
                Math.Max(appDesired, handlerDesired),
                Math.Max(target.Revision.Spec.Scaling.MinInstances, 1),
                target.Revision.Spec.Scaling.MaxInstances);
            var knownReplicas = replicas.Desired;
            if (desired > knownReplicas)
                await ApplyFreshScaleAsync(
                    target,
                    subject,
                    coordinationKey,
                    state,
                    ownership,
                    desired,
                    replicas.Desired,
                    quiescingEpoch: null,
                    cancellationToken);
            return;
        }

        if (target.IsServing && replicas.Desired < target.Revision.Spec.Scaling.MinInstances)
        {
            await ApplyFreshScaleAsync(
                target,
                subject,
                coordinationKey,
                state,
                ownership,
                target.Revision.Spec.Scaling.MinInstances,
                replicas.Desired,
                quiescingEpoch: null,
                cancellationToken);
            return;
        }

        if (!observation.Complete || !IsFullyReady(replicas, readyReplicas))
            return;

        var now = timeProvider.GetUtcNow();
        state.IdleSinceUtc ??= now;
        var scaleDownDelay = TimeSpan.FromSeconds(target.Revision.Spec.Scaling.ScaleDownDelaySeconds);
        if (now - state.IdleSinceUtc < scaleDownDelay)
            return;

        var idleDesired = target.IsServing ? target.Revision.Spec.Scaling.MinInstances : 0;
        var current = replicas.Desired;
        if (current != idleDesired)
        {
            await BeginScaleDownAsync(
                target,
                subject,
                service,
                coordinationKey,
                state,
                ownership,
                idleDesired,
                cancellationToken);
            return;
        }

        await RetireIfEligibleAsync(target, replicas, readyReplicas, cancellationToken);
    }

    private async Task<FunctionScalerOwnershipToken?> EnsureOwnershipAsync(
        FunctionRevisionCoordinationKey key,
        RevisionState state,
        CancellationToken cancellationToken)
    {
        try
        {
            if (state.Ownership is { } current)
            {
                var renewed = await scalingCoordinator.RenewOwnershipAsync(key, current, cancellationToken);
                if (TryGetOwnedToken(renewed, state, out var renewedToken))
                {
                    state.AdoptOwnership(renewedToken);
                    return renewedToken;
                }

                // Preserve idle evidence while this scaler immediately reacquires an expired
                // lease from the same coordinator incarnation. A rejected live lease, failed
                // reacquisition, or new incarnation still invalidates all local timing.
                if (current.ExpiresAtUtc <= timeProvider.GetUtcNow())
                    state.ClearOwnership();
                else
                    state.LoseOwnership();
            }

            var acquired = await scalingCoordinator.TryAcquireOwnershipAsync(
                key,
                new FunctionScalerOwnershipRequest(state.OwnershipOperationId, ownerId),
                cancellationToken);
            if (!TryGetOwnedToken(acquired, state, out var acquiredToken))
            {
                state.LoseOwnership();
                return null;
            }

            state.AdoptOwnership(acquiredToken);
            return acquiredToken;
        }
        catch
        {
            state.LoseOwnership();
            throw;
        }
    }

    private bool TryGetOwnedToken(
        FunctionScalerOwnershipDecision decision,
        RevisionState state,
        out FunctionScalerOwnershipToken token)
    {
        token = decision.Token!;
        return decision.Kind == FunctionScalerOwnershipDecisionKind.Granted && token is not null &&
            token.ExpiresAtUtc > timeProvider.GetUtcNow() &&
            string.Equals(token.OperationId, state.OwnershipOperationId, StringComparison.Ordinal) &&
            string.Equals(token.OwnerId, ownerId, StringComparison.Ordinal);
    }

    private async Task<bool> RecoverTransitionAsync(
        FunctionScaleTarget target,
        FunctionRevisionInKubernetes subject,
        FunctionServiceKey service,
        FunctionRevisionCoordinationKey coordinationKey,
        RevisionState state,
        FunctionScalerOwnershipToken ownership,
        FunctionRevisionCoordinatorSnapshot coordination,
        FunctionReplicaState replicas,
        int readyReplicas,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var pendingScale = coordination.PendingScaleIntent is { } pending && pending.ExpiresAtUtc > now
            ? pending
            : null;
        var quiescing = coordination.QuiescingIntent is { } barrier && barrier.ExpiresAtUtc > now ? barrier : null;

        if (pendingScale is not null)
        {
            state.AdoptScaleOperation(pendingScale);
            if (pendingScale.QuiescingEpoch is null)
            {
                await ApplyScaleOperationAsync(
                    target,
                    subject,
                    coordinationKey,
                    state,
                    ownership,
                    new ScaleOperation(pendingScale.OperationId, pendingScale.DesiredReplicas, QuiescingEpoch: null),
                    replicas.Desired,
                    cancellationToken);
                return true;
            }

            if (quiescing is null || quiescing.Epoch != pendingScale.QuiescingEpoch)
                return true;
            state.AdoptQuiescingOperation(quiescing.OperationId);

            if (replicas.CoordinationGeneration == pendingScale.Generation &&
                replicas.Desired == pendingScale.DesiredReplicas)
            {
                var completed = await CompleteScaleAsync(
                    coordinationKey,
                    pendingScale.IntentId,
                    pendingScale.Generation,
                    ownership,
                    FunctionCoordinationCompletionOutcome.Applied,
                    cancellationToken);
                if (completed)
                {
                    state.LastRequestedReplicas = pendingScale.DesiredReplicas;
                    state.ClearScaleOperation();
                }

                return true;
            }

            if (replicas.CoordinationGeneration > pendingScale.Generation)
                return true;

            await ContinueScaleDownAsync(
                target,
                subject,
                service,
                coordinationKey,
                state,
                ownership,
                ToLease(quiescing),
                pendingScale.DesiredReplicas,
                pendingScale,
                cancellationToken);
            return true;
        }

        if (quiescing is null)
            return false;

        state.AdoptQuiescingOperation(quiescing.OperationId);
        var idleDesired = target.IsServing ? target.Revision.Spec.Scaling.MinInstances : 0;
        if (coordination.DesiredReplicas == idleDesired)
        {
            if (HasConverged(replicas, readyReplicas, idleDesired) && await CompleteQuiescingAsync(
                    coordinationKey,
                    ownership,
                    ToLease(quiescing),
                    FunctionQuiescingCompletionOutcome.Converged,
                    cancellationToken))
            {
                state.ClearQuiescingOperation();
                await RetireIfEligibleAsync(target, replicas, readyReplicas, cancellationToken);
            }

            return true;
        }

        await ContinueScaleDownAsync(
            target,
            subject,
            service,
            coordinationKey,
            state,
            ownership,
            ToLease(quiescing),
            idleDesired,
            pendingScale: null,
            cancellationToken);
        return true;
    }

    private async Task BeginScaleDownAsync(
        FunctionScaleTarget target,
        FunctionRevisionInKubernetes subject,
        FunctionServiceKey service,
        FunctionRevisionCoordinationKey coordinationKey,
        RevisionState state,
        FunctionScalerOwnershipToken ownership,
        int desiredReplicas,
        CancellationToken cancellationToken)
    {
        var decision = await scalingCoordinator.BeginQuiescingAsync(
            coordinationKey,
            new FunctionQuiescingBeginRequest(state.GetOrCreateQuiescingOperationId(), ownership),
            cancellationToken);
        if (decision.Kind != FunctionQuiescingDecisionKind.Granted || decision.Lease is null)
        {
            if (decision.Kind is FunctionQuiescingDecisionKind.Stale or FunctionQuiescingDecisionKind.Rejected)
                state.ClearQuiescingOperation();
            return;
        }

        await ContinueScaleDownAsync(
            target,
            subject,
            service,
            coordinationKey,
            state,
            ownership,
            decision.Lease,
            desiredReplicas,
            pendingScale: null,
            cancellationToken);
    }

    private async Task ContinueScaleDownAsync(
        FunctionScaleTarget target,
        FunctionRevisionInKubernetes subject,
        FunctionServiceKey service,
        FunctionRevisionCoordinationKey coordinationKey,
        RevisionState state,
        FunctionScalerOwnershipToken ownership,
        FunctionQuiescingLease quiescing,
        int desiredReplicas,
        FunctionScaleIntent? pendingScale,
        CancellationToken cancellationToken)
    {
        var guarded = await ObserveUnderBarrierAsync(
            target,
            subject,
            service,
            coordinationKey,
            state,
            quiescing,
            cancellationToken);
        if (guarded is null || !guarded.Demand.Complete || guarded.Demand.Total != 0 || !IsFullyReady(
                guarded.Replicas,
                guarded.Endpoints.Endpoints.Length))
        {
            state.IdleSinceUtc = null;
            if (pendingScale is not null)
                // A persisted scale intent may already have crossed the Kubernetes boundary.
                // Keep its quiescing barrier until the fenced generation is observed or the
                // intent expires; aborting here could reopen admissions before a late scale-to-zero.
                return;
            await AbortScaleDownAsync(coordinationKey, state, ownership, quiescing, pendingScale, cancellationToken);
            return;
        }

        if (guarded.Replicas.Desired == desiredReplicas && pendingScale is not null &&
            guarded.Replicas.CoordinationGeneration == pendingScale.Generation)
        {
            await CompleteScaleAsync(
                coordinationKey,
                pendingScale.IntentId,
                pendingScale.Generation,
                ownership,
                FunctionCoordinationCompletionOutcome.Applied,
                cancellationToken);
            state.ClearScaleOperation();
            return;
        }

        ScaleApplyOutcome outcome;
        if (pendingScale is not null)
            outcome = await ApplyScaleOperationAsync(
                guarded.Target,
                subject,
                coordinationKey,
                state,
                ownership,
                new ScaleOperation(pendingScale.OperationId, pendingScale.DesiredReplicas, pendingScale.QuiescingEpoch),
                guarded.Replicas.Desired,
                cancellationToken);
        else
            outcome = await ApplyFreshScaleAsync(
                guarded.Target,
                subject,
                coordinationKey,
                state,
                ownership,
                desiredReplicas,
                guarded.Replicas.Desired,
                quiescing.Epoch,
                cancellationToken);

        if (outcome == ScaleApplyOutcome.Failed)
        {
            await CompleteQuiescingAsync(
                coordinationKey,
                ownership,
                quiescing,
                FunctionQuiescingCompletionOutcome.Aborted,
                cancellationToken);
            state.ClearQuiescingOperation();
        }
        else if (outcome == ScaleApplyOutcome.Deferred && pendingScale is null)
        {
            await CompleteQuiescingAsync(
                coordinationKey,
                ownership,
                quiescing,
                FunctionQuiescingCompletionOutcome.Aborted,
                cancellationToken);
            state.ClearQuiescingOperation();
        }
    }

    private async Task<GuardedScaleDownObservation?> ObserveUnderBarrierAsync(
        FunctionScaleTarget originalTarget,
        FunctionRevisionInKubernetes subject,
        FunctionServiceKey service,
        FunctionRevisionCoordinationKey coordinationKey,
        RevisionState state,
        FunctionQuiescingLease quiescing,
        CancellationToken cancellationToken)
    {
        var targets = await targetSource.ListAsync(cancellationToken);
        var refreshedTarget = targets.SingleOrDefault(candidate => Key(candidate) == Key(originalTarget));
        if (refreshedTarget is null)
            return null;

        var endpoints = endpointRegistry.GetSnapshot(service);
        var replicasResult = await kubernetesManager.ReadReplicaStateAsync(subject, cancellationToken);
        if (replicasResult is FunctionRevisionKubernetesOperationError)
            return null;

        var replicas = (FunctionReplicaState)replicasResult.Value;

        var coordination = await scalingCoordinator.GetSnapshotAsync(coordinationKey, cancellationToken);
        if (!state.ObserveCoordinator(coordination.CoordinatorIncarnation) ||
            coordination.QuiescingIntent is not { } current || current.ExpiresAtUtc <= timeProvider.GetUtcNow() ||
            current.Epoch != quiescing.Epoch || !string.Equals(
                current.LeaseId,
                quiescing.LeaseId,
                StringComparison.Ordinal))
            return null;

        var demand = await ObserveDemandAsync(
            refreshedTarget,
            endpoints,
            coordination.Admissions,
            state,
            cancellationToken);
        return new GuardedScaleDownObservation(refreshedTarget, coordination, endpoints, replicas, demand);
    }

    private async Task AbortScaleDownAsync(
        FunctionRevisionCoordinationKey coordinationKey,
        RevisionState state,
        FunctionScalerOwnershipToken ownership,
        FunctionQuiescingLease quiescing,
        FunctionScaleIntent? pendingScale,
        CancellationToken cancellationToken)
    {
        if (pendingScale is not null)
        {
            var completed = await CompleteScaleAsync(
                coordinationKey,
                pendingScale.IntentId,
                pendingScale.Generation,
                ownership,
                FunctionCoordinationCompletionOutcome.Failed,
                cancellationToken);
            if (!completed)
                return;
            state.ClearScaleOperation();
        }

        if (await CompleteQuiescingAsync(
                coordinationKey,
                ownership,
                quiescing,
                FunctionQuiescingCompletionOutcome.Aborted,
                cancellationToken))
            state.ClearQuiescingOperation();
    }

    private Task<ScaleApplyOutcome> ApplyFreshScaleAsync(
        FunctionScaleTarget target,
        FunctionRevisionInKubernetes subject,
        FunctionRevisionCoordinationKey coordinationKey,
        RevisionState state,
        FunctionScalerOwnershipToken ownership,
        int replicas,
        int observedReplicas,
        long? quiescingEpoch,
        CancellationToken cancellationToken) => ApplyScaleOperationAsync(
        target,
        subject,
        coordinationKey,
        state,
        ownership,
        state.GetOrCreateScaleOperation(replicas, quiescingEpoch),
        observedReplicas,
        cancellationToken);

    private async Task<ScaleApplyOutcome> ApplyScaleOperationAsync(
        FunctionScaleTarget target,
        FunctionRevisionInKubernetes subject,
        FunctionRevisionCoordinationKey coordinationKey,
        RevisionState state,
        FunctionScalerOwnershipToken ownership,
        ScaleOperation operation,
        int observedReplicas,
        CancellationToken cancellationToken)
    {
        var begin = await scalingCoordinator.BeginScaleAsync(
            coordinationKey,
            new FunctionScaleBeginRequest(
                operation.OperationId,
                ownership,
                operation.DesiredReplicas,
                operation.QuiescingEpoch,
                observedReplicas),
            cancellationToken);
        if (begin.Kind == FunctionScaleBeginDecisionKind.AlreadyDesired)
        {
            state.LastRequestedReplicas = operation.DesiredReplicas;
            state.ClearScaleOperation();
            return ScaleApplyOutcome.Applied;
        }

        if (begin.Kind != FunctionScaleBeginDecisionKind.Granted || begin.Lease is null)
        {
            if (begin.Kind is FunctionScaleBeginDecisionKind.Stale or FunctionScaleBeginDecisionKind.Rejected)
                state.ClearScaleOperation();
            return ScaleApplyOutcome.Deferred;
        }

        var lease = begin.Lease;
        var result = await kubernetesManager.ScaleAsync(
            subject,
            lease.DesiredReplicas,
            lease.Generation,
            cancellationToken);
        if (result is FunctionRevisionKubernetesOperationError failure)
        {
            logger.LogWarning(
                "[function-scaler-scale-failed] Failed to scale Function revision {RevisionId} to {Replicas}: {Message}",
                target.Revision.Id,
                lease.DesiredReplicas,
                failure.Message());

            if (failure is not FunctionRevisionKubernetesOperationInvalid)
            {
                // Kubernetes may have accepted the write even when the client observed a
                // timeout or conflict. Only the fenced generation plus desired replicas can
                // prove the command was applied; otherwise recovery owns the pending intent.
                var observedResult = await kubernetesManager.ReadReplicaStateAsync(subject, cancellationToken);
                if (observedResult is FunctionReplicaState observed &&
                    observed.CoordinationGeneration == lease.Generation && observed.Desired == lease.DesiredReplicas)
                {
                    var observedApplied = await CompleteScaleAsync(
                        coordinationKey,
                        lease.IntentId,
                        lease.Generation,
                        ownership,
                        FunctionCoordinationCompletionOutcome.Applied,
                        cancellationToken);
                    if (observedApplied)
                    {
                        state.LastRequestedReplicas = lease.DesiredReplicas;
                        state.ClearScaleOperation();
                        return ScaleApplyOutcome.Applied;
                    }
                }

                return ScaleApplyOutcome.Uncertain;
            }

            var failed = await CompleteScaleAsync(
                coordinationKey,
                lease.IntentId,
                lease.Generation,
                ownership,
                FunctionCoordinationCompletionOutcome.Failed,
                cancellationToken);
            if (failed)
                state.ClearScaleOperation();
            return failed ? ScaleApplyOutcome.Failed : ScaleApplyOutcome.Uncertain;
        }

        var applied = await CompleteScaleAsync(
            coordinationKey,
            lease.IntentId,
            lease.Generation,
            ownership,
            FunctionCoordinationCompletionOutcome.Applied,
            cancellationToken);
        if (!applied)
            return ScaleApplyOutcome.Uncertain;

        state.LastRequestedReplicas = lease.DesiredReplicas;
        state.ClearScaleOperation();
        logger.LogInformation(
            "[function-scaler-scaled] Scaled Function revision {RevisionId} to {Replicas} at generation {Generation}",
            target.Revision.Id,
            lease.DesiredReplicas,
            lease.Generation);
        return ScaleApplyOutcome.Applied;
    }

    private async Task<bool> CompleteScaleAsync(
        FunctionRevisionCoordinationKey key,
        string intentId,
        long generation,
        FunctionScalerOwnershipToken ownership,
        FunctionCoordinationCompletionOutcome outcome,
        CancellationToken cancellationToken)
    {
        var completed = await scalingCoordinator.CompleteScaleAsync(
            key,
            new FunctionScaleCompleteRequest(intentId, generation, outcome, ownership),
            cancellationToken);
        return completed is FunctionScaleCompleteDecision.Completed or FunctionScaleCompleteDecision.AlreadyCompleted;
    }

    private async Task<bool> CompleteQuiescingAsync(
        FunctionRevisionCoordinationKey key,
        FunctionScalerOwnershipToken ownership,
        FunctionQuiescingLease quiescing,
        FunctionQuiescingCompletionOutcome outcome,
        CancellationToken cancellationToken)
    {
        var completed = await scalingCoordinator.CompleteQuiescingAsync(
            key,
            new FunctionQuiescingCompleteRequest(ownership, quiescing.LeaseId, quiescing.Epoch, outcome),
            cancellationToken);
        return completed == FunctionQuiescingCompleteDecision.Completed;
    }

    private async Task RetireIfEligibleAsync(
        FunctionScaleTarget target,
        FunctionReplicaState replicas,
        int readyReplicas,
        CancellationToken cancellationToken)
    {
        if (target.IsServing || target.Revision.Status is not DrainingFunctionRevisionStatus ||
            !HasConverged(replicas, readyReplicas, 0))
            return;

        var retired = await targetSource.MarkRetiredAsync(target, cancellationToken);
        if (retired is not FunctionRevisionLifecycleCompleted)
            logger.LogWarning(
                "[function-scaler-retire-failed] Failed to retire Function revision {RevisionId}: {Message}",
                target.Revision.Id,
                retired.FailureMessage());
    }

    private async Task<DemandObservation> ObserveDemandAsync(
        FunctionScaleTarget target,
        FunctionEndpointSnapshot snapshot,
        FunctionAdmissionSnapshot admission,
        RevisionState state,
        CancellationToken cancellationToken)
    {
        var durablePending = target.DurableDemandByHandler.Values.Sum();
        var pending = checked(admission.Requests + durablePending);
        var durableHandlerDemand = new Dictionary<string, int>(target.DurableDemandByHandler, StringComparer.Ordinal);
        foreach (var (handlerName, handlerPending) in admission.HandlerRequests)
            durableHandlerDemand[handlerName]
                = checked(durableHandlerDemand.GetValueOrDefault(handlerName) + handlerPending);
        if (snapshot.Endpoints.IsDefaultOrEmpty)
        {
            var complete = state.LastRequestedReplicas is null or 0;
            return new DemandObservation(pending, 0, durableHandlerDemand, complete);
        }

        var observations = await Task.WhenAll(
            snapshot.Endpoints.Select(endpoint => GetStatusAsync(endpoint, target, cancellationToken)));
        var completeObservations = observations.Where(value => value is not null).Select(value => value!).ToArray();
        var inFlight = completeObservations.Sum(status =>
            checked(status.AppCapacity.Reserved + status.AppCapacity.Started));
        var handlers = completeObservations.SelectMany(status => status.Handlers)
            .GroupBy(handler => handler.HandlerName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(handler => checked(handler.Capacity.Reserved + handler.Capacity.Started)),
                StringComparer.Ordinal);
        foreach (var (handlerName, handlerPending) in durableHandlerDemand)
            handlers[handlerName] = checked(handlers.GetValueOrDefault(handlerName) + handlerPending);
        return new DemandObservation(
            pending,
            inFlight,
            handlers,
            completeObservations.Length == snapshot.Endpoints.Length);
    }

    private async Task<FunctionAgentStatusResponse?> GetStatusAsync(
        FunctionEndpoint endpoint,
        FunctionScaleTarget target,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.AgentStatusTimeoutSeconds));
        try
        {
            var response = await agentClient.GetStatusAsync(endpoint, target.App, target.Revision, timeout.Token);
            if (!response.IsSuccess || response.StatusCode != HttpStatusCode.OK || response.Value is null ||
                !string.Equals(
                    response.Value.ProtocolVersion,
                    PoleFunctionProtocol.Version,
                    StringComparison.Ordinal) ||
                !string.Equals(response.Value.AgentInstanceId, endpoint.TargetUid, StringComparison.Ordinal) ||
                response.Value.LifecycleState != FunctionAgentLifecycleState.Accepting ||
                !IsValidCapacity(response.Value.AppCapacity) || response.Value.Handlers is null ||
                response.Value.Handlers.Any(handler =>
                    string.IsNullOrWhiteSpace(handler.HandlerName) || !IsValidCapacity(handler.Capacity)))
                return null;
            return response.Value;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (FunctionAgentProtocolException)
        {
            return null;
        }
    }

    private static int DivideRoundUp(int value, int divisor) => checked((value + divisor - 1) / divisor);

    private static bool IsFullyReady(FunctionReplicaState replicas, int readyEndpoints) =>
        replicas.GenerationObserved && replicas.Desired == replicas.Current && replicas.Current == replicas.Ready &&
        replicas.Ready == replicas.Available && replicas.Available == readyEndpoints;

    private static bool HasConverged(
        FunctionReplicaState? replicas,
        int readyEndpoints,
        int expected) => replicas is not null && replicas.Desired == expected && IsFullyReady(replicas, readyEndpoints);

    private static bool IsValidCapacity(FunctionCapacityStatus capacity) =>
        capacity is { Limit: > 0, Reserved: >= 0, Started: >= 0, Available: >= 0 } &&
        capacity.Reserved + capacity.Started <= capacity.Limit &&
        capacity.Available == capacity.Limit - capacity.Reserved - capacity.Started;

    private static string Key(FunctionScaleTarget target) =>
        $"{target.App.TenantId}/{target.App.Id}/{target.Revision.Id}";

    private static FunctionRevisionCoordinationKey CoordinationKey(FunctionScaleTarget target) => new(
        target.App.TenantId,
        target.App.Id,
        target.Revision.Id);

    private static FunctionQuiescingLease ToLease(FunctionQuiescingIntent intent) => new(
        intent.OperationId,
        intent.LeaseId,
        intent.Epoch,
        intent.ExpiresAtUtc);

    private enum ScaleApplyOutcome
    {
        Applied,
        Failed,
        Deferred,
        Uncertain,
    }

    private sealed record ScaleOperation(
        string OperationId,
        int DesiredReplicas,
        long? QuiescingEpoch
    );

    private sealed class RevisionState(
        string ownershipOperationId
    )
    {
        private ScaleOperation? scaleOperation;

        public string OwnershipOperationId { get; } = ownershipOperationId;
        public FunctionScalerOwnershipToken? Ownership { get; private set; }
        public string? CoordinatorIncarnation { get; private set; }
        public int? LastRequestedReplicas { get; set; }
        public DateTimeOffset? IdleSinceUtc { get; set; }
        public string? QuiescingOperationId { get; private set; }

        public void AdoptOwnership(FunctionScalerOwnershipToken token)
        {
            if (CoordinatorIncarnation is not null && !string.Equals(
                    CoordinatorIncarnation,
                    token.CoordinatorIncarnation,
                    StringComparison.Ordinal))
                ResetLocalTiming();

            Ownership = token;
            CoordinatorIncarnation = token.CoordinatorIncarnation;
        }

        public bool ObserveCoordinator(string incarnation)
        {
            if (string.Equals(CoordinatorIncarnation, incarnation, StringComparison.Ordinal))
                return true;
            LoseOwnership();
            CoordinatorIncarnation = incarnation;
            return false;
        }

        public void LoseOwnership()
        {
            Ownership = null;
            ResetLocalTiming();
        }

        public void ClearOwnership() => Ownership = null;

        public string GetOrCreateQuiescingOperationId() =>
            QuiescingOperationId ??= $"function-quiescing-{Guid.NewGuid():N}";

        public void AdoptQuiescingOperation(string operationId) => QuiescingOperationId = operationId;

        public void ClearQuiescingOperation() => QuiescingOperationId = null;

        public ScaleOperation GetOrCreateScaleOperation(int desiredReplicas, long? quiescingEpoch)
        {
            if (scaleOperation is null || scaleOperation.DesiredReplicas != desiredReplicas ||
                scaleOperation.QuiescingEpoch != quiescingEpoch)
                scaleOperation = new ScaleOperation(
                    $"function-scale-{Guid.NewGuid():N}",
                    desiredReplicas,
                    quiescingEpoch);

            return scaleOperation;
        }

        public void AdoptScaleOperation(FunctionScaleIntent intent) => scaleOperation = new ScaleOperation(
            intent.OperationId,
            intent.DesiredReplicas,
            intent.QuiescingEpoch);

        public void ClearScaleOperation() => scaleOperation = null;

        private void ResetLocalTiming()
        {
            LastRequestedReplicas = null;
            IdleSinceUtc = null;
            QuiescingOperationId = null;
            scaleOperation = null;
        }
    }

    private sealed record DemandObservation(
        int Pending,
        int InFlight,
        IReadOnlyDictionary<string, int> HandlerDemand,
        bool Complete
    )
    {
        public int Total => checked(Pending + InFlight);
    }

    private sealed record GuardedScaleDownObservation(
        FunctionScaleTarget Target,
        FunctionRevisionCoordinatorSnapshot Coordination,
        FunctionEndpointSnapshot Endpoints,
        FunctionReplicaState Replicas,
        DemandObservation Demand
    );
}

public sealed class FunctionScalerWorker(
    IFunctionScaler scaler,
    FunctionScalerOptions options,
    TimeProvider timeProvider,
    ILogger<FunctionScalerWorker> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.PollIntervalSeconds), timeProvider);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await scaler.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "[function-scaler-cycle-failed] Function scaler cycle failed");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
                break;
        }
    }
}
