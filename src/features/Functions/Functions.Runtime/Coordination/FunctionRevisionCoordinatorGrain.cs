using Orleans.Runtime;
using Pole.Features.Functions.Runtime.Gateway;

namespace Pole.Features.Functions.Runtime.Coordination;

public sealed class FunctionRevisionCoordinatorGrain(
    [PersistentState(
        FunctionCoordinationStorage.RevisionStateName,
        FunctionCoordinationStorage.ProviderName)]
    IPersistentState<FunctionRevisionCoordinatorState> persistentState,
    FunctionGatewayOptions gatewayOptions,
    FunctionCoordinationOptions coordinationOptions,
    TimeProvider timeProvider) : Grain, IFunctionRevisionCoordinatorGrain
{
    private sealed record AdmissionFingerprint(
        string InvocationId,
        string HandlerName,
        FunctionAdmissionTriggerKind TriggerKind,
        long ReservedBytes);

    private sealed record AdmissionLease(
        AdmissionFingerprint Fingerprint,
        FunctionAdmissionToken Token);

    private readonly Dictionary<string, AdmissionLease> admissions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> handlerAdmissions = new(StringComparer.Ordinal);
    private FunctionRevisionCoordinationKey key = null!;
    private string coordinatorIncarnation = string.Empty;
    private DateTimeOffset scaleDownBlockedUntilUtc;
    private FunctionScalerOwnershipToken? scalerOwnership;
    private int admissionRequests;
    private long admissionBytes;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (!FunctionRevisionCoordinationKey.TryParse(this.GetPrimaryKeyString(), out var parsedKey))
        {
            throw new InvalidOperationException(
                $"Invalid Function revision coordinator grain key '{this.GetPrimaryKeyString()}'.");
        }

        key = parsedKey!;
        coordinatorIncarnation = Guid.NewGuid().ToString("N");
        scaleDownBlockedUntilUtc = timeProvider.GetUtcNow()
            .AddSeconds(coordinationOptions.RecoveryGraceSeconds);
        if (persistentState.State.SchemaVersion > FunctionRevisionCoordinatorState.CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Function revision coordination schema {persistentState.State.SchemaVersion} is newer than " +
                $"supported schema {FunctionRevisionCoordinatorState.CurrentSchemaVersion}.");
        }

        if (persistentState.State.SchemaVersion == 0)
            persistentState.State.SchemaVersion = FunctionRevisionCoordinatorState.CurrentSchemaVersion;

        return base.OnActivateAsync(cancellationToken);
    }

    public async Task<FunctionAdmissionDecision> AcquireAdmissionAsync(FunctionAdmissionRequest request)
    {
        var now = timeProvider.GetUtcNow();
        PurgeExpiredAdmissions(now);
        if (!IsValid(request))
            return AdmissionRejected(FunctionAdmissionRejection.InvalidRequest);

        var fingerprint = new AdmissionFingerprint(
            request.InvocationId,
            request.HandlerName,
            request.TriggerKind,
            request.ReservedBytes);
        if (admissions.TryGetValue(request.AdmissionId, out var existing))
        {
            return existing.Fingerprint == fingerprint
                ? new FunctionAdmissionDecision(
                    FunctionAdmissionDecisionKind.Granted,
                    existing.Token,
                    FunctionAdmissionRejection.None,
                    0,
                    persistentState.State.AdmissionFenceRequired)
                : AdmissionRejected(FunctionAdmissionRejection.IdempotencyConflict);
        }

        if (GetActiveScaleDownBarrierExpiry(persistentState.State, now) is { } barrierExpiresAtUtc)
        {
            return AdmissionRejected(
                FunctionAdmissionRejection.RevisionQuiescing,
                RetryAfterMilliseconds(barrierExpiresAtUtc, now));
        }

        if (admissionRequests >= gatewayOptions.MaxPendingRequestsPerRevision)
        {
            return AdmissionRejected(
                FunctionAdmissionRejection.RequestLimitExceeded,
                GetAdmissionRetryAfterMilliseconds(now));
        }

        if (request.ReservedBytes > gatewayOptions.MaxPendingBytesPerRevision - admissionBytes)
        {
            return AdmissionRejected(
                FunctionAdmissionRejection.ByteLimitExceeded,
                GetAdmissionRetryAfterMilliseconds(now));
        }

        var clearedExpiredBarrier = HasExpiredScaleDownBarrier(persistentState.State, now);
        if (clearedExpiredBarrier)
        {
            await PersistAsync(state =>
            {
                CleanExpiredTransitions(state, now);
                // A timed-out scale-down can still have an external Kubernetes replace in flight.
                // Admissions must force a newer generation write before user code may start.
                state.AdmissionFenceRequired = true;
            });
        }

        var token = new FunctionAdmissionToken(
            request.AdmissionId,
            Guid.NewGuid().ToString("N"),
            coordinatorIncarnation,
            now.AddSeconds(coordinationOptions.InitialAdmissionLeaseSeconds));
        admissions.Add(request.AdmissionId, new AdmissionLease(fingerprint, token));
        admissionRequests = checked(admissionRequests + 1);
        admissionBytes = checked(admissionBytes + request.ReservedBytes);
        handlerAdmissions[request.HandlerName] = checked(
            handlerAdmissions.GetValueOrDefault(request.HandlerName) + 1);
        DelayDeactivation(TimeSpan.FromSeconds(coordinationOptions.InitialAdmissionLeaseSeconds));
        return new FunctionAdmissionDecision(
            FunctionAdmissionDecisionKind.Granted,
            token,
            FunctionAdmissionRejection.None,
            0,
            persistentState.State.AdmissionFenceRequired);
    }

    public Task<FunctionAdmissionRenewalDecision> RenewAdmissionAsync(FunctionAdmissionRenewalRequest request)
    {
        var now = timeProvider.GetUtcNow();
        PurgeExpiredAdmissions(now);
        if (request.Token is null ||
            string.IsNullOrWhiteSpace(request.Token.AdmissionId) ||
            string.IsNullOrWhiteSpace(request.Token.LeaseId) ||
            string.IsNullOrWhiteSpace(request.Token.CoordinatorIncarnation))
        {
            return Task.FromResult(new FunctionAdmissionRenewalDecision(
                FunctionAdmissionRenewalDecisionKind.InvalidRequest,
                null));
        }

        if (!string.Equals(
                request.Token.CoordinatorIncarnation,
                coordinatorIncarnation,
                StringComparison.Ordinal))
        {
            return Task.FromResult(new FunctionAdmissionRenewalDecision(
                FunctionAdmissionRenewalDecisionKind.Stale,
                null));
        }

        if (!admissions.TryGetValue(request.Token.AdmissionId, out var admission))
        {
            return Task.FromResult(new FunctionAdmissionRenewalDecision(
                FunctionAdmissionRenewalDecisionKind.Expired,
                null));
        }

        if (!string.Equals(admission.Token.LeaseId, request.Token.LeaseId, StringComparison.Ordinal))
        {
            return Task.FromResult(new FunctionAdmissionRenewalDecision(
                FunctionAdmissionRenewalDecisionKind.Stale,
                null));
        }

        var renewedToken = admission.Token with
        {
            ExpiresAtUtc = now.AddSeconds(coordinationOptions.AdmissionLeaseSeconds)
        };
        admissions[request.Token.AdmissionId] = admission with { Token = renewedToken };
        DelayDeactivation(TimeSpan.FromSeconds(coordinationOptions.AdmissionLeaseSeconds));
        return Task.FromResult(new FunctionAdmissionRenewalDecision(
            FunctionAdmissionRenewalDecisionKind.Renewed,
            renewedToken));
    }

    public Task<FunctionAdmissionReleaseDecision> ReleaseAdmissionAsync(FunctionAdmissionReleaseRequest request)
    {
        var now = timeProvider.GetUtcNow();
        PurgeExpiredAdmissions(now);
        if (string.IsNullOrWhiteSpace(request.AdmissionId) ||
            string.IsNullOrWhiteSpace(request.LeaseId) ||
            string.IsNullOrWhiteSpace(request.CoordinatorIncarnation))
            return Task.FromResult(FunctionAdmissionReleaseDecision.InvalidRequest);

        if (!admissions.TryGetValue(request.AdmissionId, out var admission))
            return Task.FromResult(FunctionAdmissionReleaseDecision.AlreadyReleasedOrExpired);

        if (!string.Equals(
                request.CoordinatorIncarnation,
                coordinatorIncarnation,
                StringComparison.Ordinal))
        {
            return Task.FromResult(FunctionAdmissionReleaseDecision.Stale);
        }

        if (!string.Equals(request.LeaseId, admission.Token.LeaseId, StringComparison.Ordinal))
        {
            return Task.FromResult(FunctionAdmissionReleaseDecision.Stale);
        }

        RemoveAdmission(request.AdmissionId, admission);
        return Task.FromResult(FunctionAdmissionReleaseDecision.Released);
    }

    public async Task<FunctionActivationBeginDecision> BeginActivationAsync(
        FunctionActivationBeginRequest request)
    {
        var now = timeProvider.GetUtcNow();
        PurgeExpiredAdmissions(now);
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.DesiredReplicas <= 0)
            return ActivationRejected(FunctionActivationRejection.InvalidRequest);

        var currentActivation = persistentState.State.ActivationIntent;
        if (currentActivation is not null && IsActive(currentActivation, now))
        {
            if (currentActivation.DesiredReplicas != request.DesiredReplicas)
            {
                return string.Equals(
                    currentActivation.OperationId,
                    request.OperationId,
                    StringComparison.Ordinal)
                    ? ActivationRejected(FunctionActivationRejection.InvalidRequest)
                    : ActivationBusy(currentActivation.ExpiresAtUtc, now);
            }

            return new FunctionActivationBeginDecision(
                FunctionActivationBeginDecisionKind.Granted,
                ToLease(currentActivation),
                FunctionActivationRejection.None,
                0);
        }

        var pendingScale = persistentState.State.PendingScaleIntent;
        if (pendingScale is not null && IsActive(pendingScale, now))
        {
            return new FunctionActivationBeginDecision(
                FunctionActivationBeginDecisionKind.Busy,
                null,
                FunctionActivationRejection.ScaleTransitionInProgress,
                RetryAfterMilliseconds(pendingScale.ExpiresAtUtc, now));
        }

        FunctionActivationIntent? activation = null;
        await PersistAsync(state =>
        {
            CleanExpiredTransitions(state, now);
            InvalidateQuiescing(state);
            var previousDesiredReplicas = state.DesiredReplicas;
            state.Generation = checked(state.Generation + 1);
            state.DesiredReplicas = Math.Max(state.DesiredReplicas ?? 0, request.DesiredReplicas);
            activation = new FunctionActivationIntent(
                request.OperationId,
                Guid.NewGuid().ToString("N"),
                state.Generation,
                state.DesiredReplicas.Value,
                previousDesiredReplicas,
                now,
                now.AddSeconds(coordinationOptions.ActivationLeaseSeconds));
            state.ActivationIntent = activation;
        });

        DelayDeactivation(TimeSpan.FromSeconds(coordinationOptions.ActivationLeaseSeconds));
        return new FunctionActivationBeginDecision(
            FunctionActivationBeginDecisionKind.Granted,
            ToLease(activation!),
            FunctionActivationRejection.None,
            0);
    }

    public async Task<FunctionActivationCompleteDecision> CompleteActivationAsync(
        FunctionActivationCompleteRequest request)
    {
        var now = timeProvider.GetUtcNow();
        if (string.IsNullOrWhiteSpace(request.LeaseId) || request.Generation <= 0)
            return FunctionActivationCompleteDecision.InvalidRequest;

        var lastCompletion = persistentState.State.LastActivationCompletion;
        if (lastCompletion is not null &&
            string.Equals(lastCompletion.LeaseId, request.LeaseId, StringComparison.Ordinal) &&
            lastCompletion.Generation == request.Generation)
        {
            return lastCompletion.Outcome == request.Outcome
                ? FunctionActivationCompleteDecision.AlreadyCompleted
                : FunctionActivationCompleteDecision.Stale;
        }

        var activation = persistentState.State.ActivationIntent;
        if (activation is null ||
            !IsActive(activation, now) ||
            !string.Equals(activation.LeaseId, request.LeaseId, StringComparison.Ordinal) ||
            activation.Generation != request.Generation ||
            persistentState.State.Generation != request.Generation)
        {
            return FunctionActivationCompleteDecision.Stale;
        }

        await PersistAsync(state =>
        {
            if (request.Outcome == FunctionCoordinationCompletionOutcome.Failed)
                state.DesiredReplicas = activation.PreviousDesiredReplicas;
            else
                state.AdmissionFenceRequired = false;
            state.ActivationIntent = null;
            state.LastActivationCompletion = new FunctionActivationCompletion(
                request.LeaseId,
                request.Generation,
                request.Outcome);
        });
        return FunctionActivationCompleteDecision.Completed;
    }

    public Task<FunctionScalerOwnershipDecision> TryAcquireScalerOwnershipAsync(
        FunctionScalerOwnershipRequest request)
    {
        var now = timeProvider.GetUtcNow();
        PurgeExpiredScalerOwnership(now);
        if (string.IsNullOrWhiteSpace(request.OperationId) || string.IsNullOrWhiteSpace(request.OwnerId))
            return Task.FromResult(ScalerOwnershipRejected());

        if (scalerOwnership is not null)
        {
            if (string.Equals(scalerOwnership.OperationId, request.OperationId, StringComparison.Ordinal) &&
                string.Equals(scalerOwnership.OwnerId, request.OwnerId, StringComparison.Ordinal))
            {
                return Task.FromResult(new FunctionScalerOwnershipDecision(
                    FunctionScalerOwnershipDecisionKind.Granted,
                    scalerOwnership,
                    0));
            }

            return Task.FromResult(new FunctionScalerOwnershipDecision(
                FunctionScalerOwnershipDecisionKind.Busy,
                null,
                RetryAfterMilliseconds(scalerOwnership.ExpiresAtUtc, now)));
        }

        scalerOwnership = new FunctionScalerOwnershipToken(
            request.OperationId,
            request.OwnerId,
            Guid.NewGuid().ToString("N"),
            coordinatorIncarnation,
            now.AddSeconds(coordinationOptions.ScalerOwnershipLeaseSeconds));
        DelayDeactivation(TimeSpan.FromSeconds(coordinationOptions.ScalerOwnershipLeaseSeconds));
        return Task.FromResult(new FunctionScalerOwnershipDecision(
            FunctionScalerOwnershipDecisionKind.Granted,
            scalerOwnership,
            0));
    }

    public Task<FunctionScalerOwnershipDecision> RenewScalerOwnershipAsync(
        FunctionScalerOwnershipToken token)
    {
        var now = timeProvider.GetUtcNow();
        PurgeExpiredScalerOwnership(now);
        if (!IsValidScalerToken(token, now))
            return Task.FromResult(ScalerOwnershipRejected());

        scalerOwnership = scalerOwnership! with
        {
            ExpiresAtUtc = now.AddSeconds(coordinationOptions.ScalerOwnershipLeaseSeconds)
        };
        DelayDeactivation(TimeSpan.FromSeconds(coordinationOptions.ScalerOwnershipLeaseSeconds));
        return Task.FromResult(new FunctionScalerOwnershipDecision(
            FunctionScalerOwnershipDecisionKind.Granted,
            scalerOwnership,
            0));
    }

    public Task<FunctionScalerOwnershipReleaseDecision> ReleaseScalerOwnershipAsync(
        FunctionScalerOwnershipToken token)
    {
        var now = timeProvider.GetUtcNow();
        PurgeExpiredScalerOwnership(now);
        if (token is null ||
            string.IsNullOrWhiteSpace(token.OperationId) ||
            string.IsNullOrWhiteSpace(token.OwnerId) ||
            string.IsNullOrWhiteSpace(token.LeaseId) ||
            string.IsNullOrWhiteSpace(token.CoordinatorIncarnation))
        {
            return Task.FromResult(FunctionScalerOwnershipReleaseDecision.InvalidRequest);
        }

        if (scalerOwnership is null)
            return Task.FromResult(FunctionScalerOwnershipReleaseDecision.AlreadyReleasedOrExpired);
        if (!IsValidScalerToken(token, now))
            return Task.FromResult(FunctionScalerOwnershipReleaseDecision.Stale);

        scalerOwnership = null;
        return Task.FromResult(FunctionScalerOwnershipReleaseDecision.Released);
    }

    public async Task<FunctionQuiescingDecision> BeginQuiescingAsync(
        FunctionQuiescingBeginRequest request)
    {
        var now = timeProvider.GetUtcNow();
        PurgeExpiredAdmissions(now);
        if (string.IsNullOrWhiteSpace(request.OperationId))
            return QuiescingDecision(FunctionQuiescingDecisionKind.Rejected);
        if (!IsValidScalerToken(request.ScalerToken, now))
            return QuiescingDecision(FunctionQuiescingDecisionKind.Stale);

        var current = persistentState.State.QuiescingIntent;
        if (current is not null && IsActive(current, now))
        {
            return string.Equals(current.OperationId, request.OperationId, StringComparison.Ordinal)
                ? new FunctionQuiescingDecision(
                    FunctionQuiescingDecisionKind.Granted,
                    ToLease(current),
                    0)
                : QuiescingDecision(
                    FunctionQuiescingDecisionKind.Busy,
                    RetryAfterMilliseconds(current.ExpiresAtUtc, now));
        }

        if (now < scaleDownBlockedUntilUtc)
        {
            return QuiescingDecision(
                FunctionQuiescingDecisionKind.RecoveryGrace,
                RetryAfterMilliseconds(scaleDownBlockedUntilUtc, now));
        }

        if (admissionRequests != 0)
        {
            return QuiescingDecision(
                FunctionQuiescingDecisionKind.ActiveAdmissions,
                GetAdmissionRetryAfterMilliseconds(now));
        }

        var activation = persistentState.State.ActivationIntent;
        if (activation is not null && IsActive(activation, now))
        {
            return QuiescingDecision(
                FunctionQuiescingDecisionKind.Busy,
                RetryAfterMilliseconds(activation.ExpiresAtUtc, now));
        }

        var scale = persistentState.State.PendingScaleIntent;
        if (scale is not null && IsActive(scale, now))
        {
            return QuiescingDecision(
                FunctionQuiescingDecisionKind.Busy,
                RetryAfterMilliseconds(scale.ExpiresAtUtc, now));
        }

        FunctionQuiescingIntent? quiescing = null;
        await PersistAsync(state =>
        {
            CleanExpiredTransitions(state, now);
            state.Generation = checked(state.Generation + 1);
            quiescing = new FunctionQuiescingIntent(
                request.OperationId,
                Guid.NewGuid().ToString("N"),
                state.Generation,
                now,
                now.AddSeconds(coordinationOptions.ScaleTransitionLeaseSeconds));
            state.QuiescingIntent = quiescing;
        });
        DelayDeactivation(TimeSpan.FromSeconds(coordinationOptions.ScaleTransitionLeaseSeconds));
        return new FunctionQuiescingDecision(
            FunctionQuiescingDecisionKind.Granted,
            ToLease(quiescing!),
            0);
    }

    public async Task<FunctionQuiescingCompleteDecision> CompleteQuiescingAsync(
        FunctionQuiescingCompleteRequest request)
    {
        var now = timeProvider.GetUtcNow();
        PurgeExpiredAdmissions(now);
        if (string.IsNullOrWhiteSpace(request.LeaseId) || request.Epoch <= 0)
            return FunctionQuiescingCompleteDecision.InvalidRequest;
        if (!IsValidScalerToken(request.ScalerToken, now))
            return FunctionQuiescingCompleteDecision.Stale;

        var quiescing = persistentState.State.QuiescingIntent;
        if (quiescing is null ||
            !IsActive(quiescing, now) ||
            !string.Equals(quiescing.LeaseId, request.LeaseId, StringComparison.Ordinal) ||
            quiescing.Epoch != request.Epoch)
        {
            return FunctionQuiescingCompleteDecision.Stale;
        }

        if (persistentState.State.PendingScaleIntent is not null)
            return FunctionQuiescingCompleteDecision.Busy;
        if (admissionRequests != 0)
            return FunctionQuiescingCompleteDecision.ActiveAdmissions;

        await PersistAsync(state =>
        {
            state.QuiescingIntent = null;
            if (request.Outcome == FunctionQuiescingCompletionOutcome.Aborted)
                state.Generation = checked(state.Generation + 1);
        });
        return FunctionQuiescingCompleteDecision.Completed;
    }

    public async Task<FunctionScaleBeginDecision> BeginScaleAsync(FunctionScaleBeginRequest request)
    {
        var now = timeProvider.GetUtcNow();
        PurgeExpiredAdmissions(now);
        if (string.IsNullOrWhiteSpace(request.OperationId) ||
            request.DesiredReplicas < 0 ||
            request.ObservedReplicas < 0)
            return ScaleDecision(FunctionScaleBeginDecisionKind.Rejected);
        if (!IsValidScalerToken(request.ScalerToken, now))
            return ScaleDecision(FunctionScaleBeginDecisionKind.Stale);

        var effectiveState = persistentState.State.Copy();
        CleanExpiredTransitions(effectiveState, now);
        var currentScale = effectiveState.PendingScaleIntent;
        if (currentScale is not null && IsActive(currentScale, now))
        {
            if (currentScale.Generation != persistentState.State.Generation)
            {
                return string.Equals(currentScale.OperationId, request.OperationId, StringComparison.Ordinal)
                    ? ScaleDecision(FunctionScaleBeginDecisionKind.Stale)
                    : ScaleDecision(
                        FunctionScaleBeginDecisionKind.Busy,
                        RetryAfterMilliseconds(currentScale.ExpiresAtUtc, now));
            }

            if (!string.Equals(currentScale.OperationId, request.OperationId, StringComparison.Ordinal))
            {
                return ScaleDecision(
                    FunctionScaleBeginDecisionKind.Busy,
                    RetryAfterMilliseconds(currentScale.ExpiresAtUtc, now));
            }

            if (currentScale.DesiredReplicas != request.DesiredReplicas ||
                currentScale.QuiescingEpoch != request.QuiescingEpoch)
            {
                return ScaleDecision(FunctionScaleBeginDecisionKind.Rejected);
            }

            if (currentScale.QuiescingEpoch is { } currentEpoch &&
                (effectiveState.QuiescingIntent is not { } currentQuiescing ||
                 !IsActive(currentQuiescing, now) ||
                 currentQuiescing.Epoch != currentEpoch))
            {
                return ScaleDecision(FunctionScaleBeginDecisionKind.Stale);
            }

            return new FunctionScaleBeginDecision(
                FunctionScaleBeginDecisionKind.Granted,
                ToLease(currentScale),
                0);
        }

        var activation = effectiveState.ActivationIntent;
        if (activation is not null && IsActive(activation, now))
        {
            return ScaleDecision(
                FunctionScaleBeginDecisionKind.Busy,
                RetryAfterMilliseconds(activation.ExpiresAtUtc, now));
        }

        var isScaleDown = IsScaleDown(
            effectiveState.DesiredReplicas,
            request.ObservedReplicas,
            request.DesiredReplicas);
        FunctionQuiescingIntent? scaleDownQuiescing = null;
        if (isScaleDown)
        {
            if (now < scaleDownBlockedUntilUtc)
            {
                return ScaleDecision(
                    FunctionScaleBeginDecisionKind.RecoveryGrace,
                    RetryAfterMilliseconds(scaleDownBlockedUntilUtc, now));
            }

            if (admissionRequests != 0)
            {
                return ScaleDecision(
                    FunctionScaleBeginDecisionKind.ActiveAdmissions,
                    GetAdmissionRetryAfterMilliseconds(now));
            }

            var quiescing = effectiveState.QuiescingIntent;
            if (request.QuiescingEpoch is null)
                return ScaleDecision(FunctionScaleBeginDecisionKind.QuiescingRequired);
            if (quiescing is null ||
                !IsActive(quiescing, now) ||
                quiescing.Epoch != request.QuiescingEpoch)
                return ScaleDecision(FunctionScaleBeginDecisionKind.Stale);
            scaleDownQuiescing = quiescing;
        }
        else if (request.QuiescingEpoch is not null)
        {
            return ScaleDecision(FunctionScaleBeginDecisionKind.Rejected);
        }

        FunctionScaleIntent? scale = null;
        await PersistAsync(state =>
        {
            CleanExpiredTransitions(state, now);
            if (!isScaleDown)
                InvalidateQuiescing(state);
            var previousDesiredReplicas = state.DesiredReplicas;
            state.Generation = checked(state.Generation + 1);
            state.DesiredReplicas = request.DesiredReplicas;
            var expiresAtUtc = now.AddSeconds(coordinationOptions.ScaleTransitionLeaseSeconds);
            if (scaleDownQuiescing is not null && scaleDownQuiescing.ExpiresAtUtc < expiresAtUtc)
                expiresAtUtc = scaleDownQuiescing.ExpiresAtUtc;
            scale = new FunctionScaleIntent(
                request.OperationId,
                Guid.NewGuid().ToString("N"),
                state.Generation,
                request.DesiredReplicas,
                previousDesiredReplicas,
                isScaleDown ? request.QuiescingEpoch : null,
                now,
                expiresAtUtc);
            state.PendingScaleIntent = scale;
        });
        DelayDeactivation(scale!.ExpiresAtUtc - now);
        return new FunctionScaleBeginDecision(
            FunctionScaleBeginDecisionKind.Granted,
            ToLease(scale!),
            0);
    }

    public async Task<FunctionScaleCompleteDecision> CompleteScaleAsync(FunctionScaleCompleteRequest request)
    {
        var now = timeProvider.GetUtcNow();
        if (string.IsNullOrWhiteSpace(request.IntentId) || request.Generation <= 0)
            return FunctionScaleCompleteDecision.InvalidRequest;
        if (!IsValidScalerToken(request.ScalerToken, now))
            return FunctionScaleCompleteDecision.Stale;

        var lastCompletion = persistentState.State.LastScaleCompletion;
        if (lastCompletion is not null &&
            string.Equals(lastCompletion.IntentId, request.IntentId, StringComparison.Ordinal) &&
            lastCompletion.Generation == request.Generation)
        {
            return lastCompletion.Outcome == request.Outcome
                ? FunctionScaleCompleteDecision.AlreadyCompleted
                : FunctionScaleCompleteDecision.Stale;
        }

        var scale = persistentState.State.PendingScaleIntent;
        if (scale is null ||
            !IsActive(scale, now) ||
            !string.Equals(scale.IntentId, request.IntentId, StringComparison.Ordinal) ||
            scale.Generation != request.Generation ||
            persistentState.State.Generation != request.Generation)
        {
            return FunctionScaleCompleteDecision.Stale;
        }

        await PersistAsync(state =>
        {
            if (request.Outcome == FunctionCoordinationCompletionOutcome.Failed)
                state.DesiredReplicas = scale.PreviousDesiredReplicas;
            state.PendingScaleIntent = null;
            state.LastScaleCompletion = new FunctionScaleCompletion(
                request.IntentId,
                request.Generation,
                request.Outcome);
        });
        return FunctionScaleCompleteDecision.Completed;
    }

    public Task<FunctionRevisionCoordinatorSnapshot> GetSnapshotAsync()
    {
        var now = timeProvider.GetUtcNow();
        PurgeExpiredAdmissions(now);
        PurgeExpiredScalerOwnership(now);
        return Task.FromResult(new FunctionRevisionCoordinatorSnapshot(
            key,
            coordinatorIncarnation,
            persistentState.State.Generation,
            persistentState.State.DesiredReplicas,
            scaleDownBlockedUntilUtc,
            new FunctionAdmissionSnapshot(
                admissionRequests,
                admissionBytes,
                new Dictionary<string, int>(handlerAdmissions, StringComparer.Ordinal)),
            persistentState.State.ActivationIntent,
            persistentState.State.QuiescingIntent,
            persistentState.State.PendingScaleIntent,
            scalerOwnership));
    }

    private bool IsValid(FunctionAdmissionRequest request) =>
        !string.IsNullOrWhiteSpace(request.AdmissionId) &&
        !string.IsNullOrWhiteSpace(request.InvocationId) &&
        !string.IsNullOrWhiteSpace(request.HandlerName) &&
        request.ReservedBytes >= 0 &&
        request.ReservedBytes <= gatewayOptions.MaxRequestBodyBytes;

    private bool IsValidScalerToken(FunctionScalerOwnershipToken? token, DateTimeOffset now)
    {
        PurgeExpiredScalerOwnership(now);
        return token is not null &&
               scalerOwnership is not null &&
               token.ExpiresAtUtc > now &&
               string.Equals(token.OperationId, scalerOwnership.OperationId, StringComparison.Ordinal) &&
               string.Equals(token.OwnerId, scalerOwnership.OwnerId, StringComparison.Ordinal) &&
               string.Equals(token.LeaseId, scalerOwnership.LeaseId, StringComparison.Ordinal) &&
               string.Equals(token.CoordinatorIncarnation, coordinatorIncarnation, StringComparison.Ordinal);
    }

    private void PurgeExpiredScalerOwnership(DateTimeOffset now)
    {
        if (scalerOwnership?.ExpiresAtUtc <= now)
            scalerOwnership = null;
    }

    private void PurgeExpiredAdmissions(DateTimeOffset now)
    {
        foreach (var (admissionId, admission) in admissions
                     .Where(pair => pair.Value.Token.ExpiresAtUtc <= now)
                     .ToArray())
        {
            RemoveAdmission(admissionId, admission);
        }
    }

    private void RemoveAdmission(string admissionId, AdmissionLease admission)
    {
        admissions.Remove(admissionId);
        admissionRequests--;
        admissionBytes -= admission.Fingerprint.ReservedBytes;
        var handlerName = admission.Fingerprint.HandlerName;
        var handlerRequests = handlerAdmissions[handlerName] - 1;
        if (handlerRequests == 0)
            handlerAdmissions.Remove(handlerName);
        else
            handlerAdmissions[handlerName] = handlerRequests;
    }

    private int GetAdmissionRetryAfterMilliseconds(DateTimeOffset now)
    {
        if (admissions.Count == 0)
            return 1;
        return RetryAfterMilliseconds(
            admissions.Values.Min(admission => admission.Token.ExpiresAtUtc),
            now);
    }

    private async Task PersistAsync(Action<FunctionRevisionCoordinatorState> mutation)
    {
        var previous = persistentState.State.Copy();
        try
        {
            mutation(persistentState.State);
            persistentState.State.SchemaVersion = FunctionRevisionCoordinatorState.CurrentSchemaVersion;
            await persistentState.WriteStateAsync();
        }
        catch
        {
            persistentState.State = previous;
            DeactivateOnIdle();
            throw;
        }
    }

    private static void CleanExpiredTransitions(
        FunctionRevisionCoordinatorState state,
        DateTimeOffset now)
    {
        var changed = false;
        if (state.ActivationIntent?.ExpiresAtUtc <= now)
        {
            state.DesiredReplicas = state.ActivationIntent.PreviousDesiredReplicas;
            state.ActivationIntent = null;
            changed = true;
        }

        if (state.PendingScaleIntent?.ExpiresAtUtc <= now)
        {
            state.DesiredReplicas = state.PendingScaleIntent.PreviousDesiredReplicas;
            state.PendingScaleIntent = null;
            changed = true;
        }

        if (state.QuiescingIntent?.ExpiresAtUtc <= now)
        {
            state.QuiescingIntent = null;
            changed = true;
        }

        if (changed)
            state.Generation = checked(state.Generation + 1);
    }

    private static void InvalidateQuiescing(FunctionRevisionCoordinatorState state)
    {
        if (state.QuiescingIntent is not { } quiescing)
            return;

        state.QuiescingIntent = null;
        if (state.PendingScaleIntent?.QuiescingEpoch == quiescing.Epoch)
        {
            state.DesiredReplicas = state.PendingScaleIntent.PreviousDesiredReplicas;
            state.PendingScaleIntent = null;
        }
    }

    private static DateTimeOffset? GetActiveScaleDownBarrierExpiry(
        FunctionRevisionCoordinatorState state,
        DateTimeOffset now)
    {
        DateTimeOffset? expiresAtUtc = state.QuiescingIntent is { ExpiresAtUtc: var quiescingExpiry } &&
                                           quiescingExpiry > now
            ? quiescingExpiry
            : null;
        if (state.PendingScaleIntent is { QuiescingEpoch: not null, ExpiresAtUtc: var scaleExpiry } &&
            scaleExpiry > now &&
            (expiresAtUtc is null || scaleExpiry > expiresAtUtc))
        {
            expiresAtUtc = scaleExpiry;
        }

        return expiresAtUtc;
    }

    private static bool HasExpiredScaleDownBarrier(
        FunctionRevisionCoordinatorState state,
        DateTimeOffset now) =>
        state.QuiescingIntent?.ExpiresAtUtc <= now ||
        state.PendingScaleIntent is { QuiescingEpoch: not null } scale && scale.ExpiresAtUtc <= now;

    private static bool IsScaleDown(
        int? persistedDesiredReplicas,
        int observedReplicas,
        int requestedDesiredReplicas) =>
        requestedDesiredReplicas < Math.Max(persistedDesiredReplicas ?? 0, observedReplicas);

    private static bool IsActive(FunctionActivationIntent? intent, DateTimeOffset now) =>
        intent?.ExpiresAtUtc > now;

    private static bool IsActive(FunctionQuiescingIntent? intent, DateTimeOffset now) =>
        intent?.ExpiresAtUtc > now;

    private static bool IsActive(FunctionScaleIntent? intent, DateTimeOffset now) =>
        intent?.ExpiresAtUtc > now;

    private static FunctionActivationLease ToLease(FunctionActivationIntent intent) => new(
        intent.OperationId,
        intent.LeaseId,
        intent.Generation,
        intent.DesiredReplicas,
        intent.ExpiresAtUtc);

    private static FunctionQuiescingLease ToLease(FunctionQuiescingIntent intent) => new(
        intent.OperationId,
        intent.LeaseId,
        intent.Epoch,
        intent.ExpiresAtUtc);

    private static FunctionScaleLease ToLease(FunctionScaleIntent intent) => new(
        intent.OperationId,
        intent.IntentId,
        intent.Generation,
        intent.DesiredReplicas,
        intent.ExpiresAtUtc);

    private static FunctionAdmissionDecision AdmissionRejected(
        FunctionAdmissionRejection rejection,
        int retryAfterMilliseconds = 0) => new(
        FunctionAdmissionDecisionKind.Rejected,
        null,
        rejection,
        retryAfterMilliseconds,
        false);

    private static FunctionActivationBeginDecision ActivationRejected(
        FunctionActivationRejection rejection) => new(
        FunctionActivationBeginDecisionKind.Rejected,
        null,
        rejection,
        0);

    private static FunctionActivationBeginDecision ActivationBusy(
        DateTimeOffset expiresAtUtc,
        DateTimeOffset now) => new(
        FunctionActivationBeginDecisionKind.Busy,
        null,
        FunctionActivationRejection.None,
        RetryAfterMilliseconds(expiresAtUtc, now));

    private static FunctionScalerOwnershipDecision ScalerOwnershipRejected() => new(
        FunctionScalerOwnershipDecisionKind.Rejected,
        null,
        0);

    private static FunctionQuiescingDecision QuiescingDecision(
        FunctionQuiescingDecisionKind kind,
        int retryAfterMilliseconds = 0) => new(kind, null, retryAfterMilliseconds);

    private static FunctionScaleBeginDecision ScaleDecision(
        FunctionScaleBeginDecisionKind kind,
        int retryAfterMilliseconds = 0) => new(kind, null, retryAfterMilliseconds);

    private static int RetryAfterMilliseconds(DateTimeOffset expiresAtUtc, DateTimeOffset now)
    {
        var milliseconds = Math.Ceiling((expiresAtUtc - now).TotalMilliseconds);
        return (int)Math.Clamp(milliseconds, 1, int.MaxValue);
    }
}
