using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Gateway;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Functions.Protocol;
using Pole.ResultType;

namespace Pole.Features.Functions.Runtime.Scaling;

public sealed class FunctionScalerOptions
{
    public const string SectionName = "Functions:Scaler";

    public int PollIntervalSeconds { get; set; } = 2;
    public int AgentStatusTimeoutSeconds { get; set; } = 2;

    public void Validate()
    {
        if (PollIntervalSeconds is < 1 or > 60)
            throw new InvalidOperationException("Function scaler poll interval must be between 1 and 60 seconds.");
        if (AgentStatusTimeoutSeconds is < 1 or > 30)
            throw new InvalidOperationException("Function scaler Agent timeout must be between 1 and 30 seconds.");
    }
}

public interface IFunctionScaler
{
    Task RunOnceAsync(CancellationToken cancellationToken);
}

public sealed class FunctionScaler(
    IFunctionScaleTargetSource targetSource,
    IFunctionEndpointRegistry endpointRegistry,
    IFunctionAgentClient agentClient,
    IFunctionPendingAdmissionLimiter pendingAdmission,
    IFunctionRevisionKubernetesManager kubernetesManager,
    FunctionScalerOptions options,
    TimeProvider timeProvider,
    ILogger<FunctionScaler> logger) : IFunctionScaler, IDisposable
{
    private sealed class RevisionState : IDisposable
    {
        private FunctionScaleDownLease? scaleDownLease;

        public int? LastRequestedReplicas { get; set; }
        public DateTimeOffset? IdleSinceUtc { get; set; }
        public int? ScaleDownTarget { get; private set; }
        public FunctionScaleDownLease? ScaleDownLease => scaleDownLease;

        public void BeginScaleDown(int target, FunctionScaleDownLease lease)
        {
            ScaleDownTarget = target;
            scaleDownLease = lease;
        }

        public void EndScaleDown()
        {
            ScaleDownTarget = null;
            Interlocked.Exchange(ref scaleDownLease, null)?.Dispose();
        }

        public void Dispose() => EndScaleDown();
    }

    private sealed record DemandObservation(
        int Pending,
        int InFlight,
        IReadOnlyDictionary<string, int> HandlerDemand,
        bool Complete)
    {
        public int Total => checked(Pending + InFlight);
    }

    private readonly ConcurrentDictionary<string, RevisionState> states = new(StringComparer.Ordinal);

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var targets = await targetSource.ListAsync(cancellationToken);
        var liveKeys = targets.Select(Key).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in states.Keys.Where(key => !liveKeys.Contains(key)))
        {
            if (states.TryRemove(stale, out var staleState))
                staleState.Dispose();
        }

        foreach (var target in targets)
        {
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
    }

    private async Task ReconcileAsync(FunctionScaleTarget target, CancellationToken cancellationToken)
    {
        var subject = new FunctionRevisionInKubernetes(target.App, target.Revision);
        var service = new FunctionServiceKey(subject.Namespace, subject.ServiceName);
        var snapshot = endpointRegistry.GetSnapshot(service);
        var state = states.GetOrAdd(Key(target), _ => new RevisionState());
        var replicasResult = await kubernetesManager.ReadReplicaStateAsync(subject, cancellationToken);
        var replicas = replicasResult.TryGetValue(out var replicaState, out _) ? replicaState : null;
        var observation = await ObserveDemandAsync(target, snapshot, state, cancellationToken);
        var readyReplicas = snapshot.Endpoints.Length;

        if (state.ScaleDownLease is not null)
        {
            if (state.ScaleDownTarget is not { } scaleDownTarget ||
                !HasConverged(replicas, readyReplicas, scaleDownTarget))
                return;
            state.EndScaleDown();
        }

        if (observation.Total > 0)
        {
            state.IdleSinceUtc = null;
            var appDesired =
                DivideRoundUp(observation.Total, target.Revision.Spec.Scaling.TargetConcurrencyPerInstance);
            var handlerDesired = target.Revision.Spec.Handlers
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
            var knownReplicas = replicas?.Desired ??
                                Math.Max(readyReplicas, state.LastRequestedReplicas ?? readyReplicas);
            if (desired > knownReplicas)
                await ScaleAsync(target, subject, state, desired, cancellationToken);
            return;
        }

        if (target.IsServing && replicas is not null &&
            replicas.Desired < target.Revision.Spec.Scaling.MinInstances)
        {
            await ScaleAsync(
                target,
                subject,
                state,
                target.Revision.Spec.Scaling.MinInstances,
                cancellationToken);
            return;
        }

        if (!observation.Complete || replicas is null || !IsFullyReady(replicas, readyReplicas))
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
            var scaleDown = pendingAdmission.TryBeginScaleDown(PendingKey(target));
            if (scaleDown is null)
                return;
            var transferred = false;
            try
            {
                // The barrier blocks new gateway admissions. Re-sample every source under it so
                // no invocation can start between the final Agent status read and the scale patch.
                var guardedSnapshot = endpointRegistry.GetSnapshot(service);
                var guardedReplicasResult = await kubernetesManager.ReadReplicaStateAsync(subject, cancellationToken);
                if (!guardedReplicasResult.TryGetValue(out var guardedReplicas, out _) ||
                    !IsFullyReady(guardedReplicas, guardedSnapshot.Endpoints.Length))
                    return;
                var guardedObservation = await ObserveDemandAsync(
                    target,
                    guardedSnapshot,
                    state,
                    cancellationToken);
                if (!guardedObservation.Complete || guardedObservation.Total != 0)
                    return;
                if (guardedReplicas.Desired == idleDesired)
                    return;
                if (!await ScaleAsync(target, subject, state, idleDesired, cancellationToken))
                    return;

                state.BeginScaleDown(idleDesired, scaleDown);
                transferred = true;
            }
            finally
            {
                if (!transferred)
                    scaleDown.Dispose();
            }
            return;
        }

        if (!target.IsServing && target.Revision.State == FunctionRevisionState.Draining &&
            idleDesired == 0 &&
            HasConverged(replicas, readyReplicas, 0))
        {
            var retired = await targetSource.MarkRetiredAsync(target, cancellationToken);
            if (retired is IFailure failure)
            {
                logger.LogWarning(
                    "[function-scaler-retire-failed] Failed to retire Function revision {RevisionId}: {Message}",
                    target.Revision.Id,
                    failure.Message);
            }
        }
    }

    private async Task<DemandObservation> ObserveDemandAsync(
        FunctionScaleTarget target,
        FunctionEndpointSnapshot snapshot,
        RevisionState state,
        CancellationToken cancellationToken)
    {
        var pendingKey = PendingKey(target);
        var pendingSnapshot = pendingAdmission.GetSnapshot(pendingKey);
        var durablePending = target.DurableDemandByHandler.Values.Sum();
        var pending = checked(pendingSnapshot.Requests + durablePending);
        var durableHandlerDemand = new Dictionary<string, int>(
            target.DurableDemandByHandler,
            StringComparer.Ordinal);
        foreach (var (handlerName, handlerPending) in pendingSnapshot.HandlerRequests)
            durableHandlerDemand[handlerName] = checked(
                durableHandlerDemand.GetValueOrDefault(handlerName) + handlerPending);
        if (snapshot.Endpoints.IsDefaultOrEmpty)
        {
            var complete = state.LastRequestedReplicas is null or 0;
            return new DemandObservation(
                pending,
                0,
                durableHandlerDemand,
                complete);
        }

        var observations = await Task.WhenAll(snapshot.Endpoints.Select(endpoint =>
            GetStatusAsync(endpoint, target, cancellationToken)));
        var completeObservations = observations.Where(value => value is not null).Select(value => value!).ToArray();
        var inFlight = completeObservations.Sum(status =>
            checked(status.AppCapacity.Reserved + status.AppCapacity.Started));
        var handlers = completeObservations
            .SelectMany(status => status.Handlers)
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
            var response = await agentClient.GetStatusAsync(
                endpoint,
                target.App,
                target.Revision,
                timeout.Token);
            if (!response.IsSuccess || response.StatusCode != HttpStatusCode.OK || response.Value is null ||
                !string.Equals(response.Value.ProtocolVersion, PoleFunctionProtocol.Version, StringComparison.Ordinal) ||
                !string.Equals(response.Value.AgentInstanceId, endpoint.TargetUid, StringComparison.Ordinal) ||
                response.Value.LifecycleState != FunctionAgentLifecycleState.Accepting ||
                !IsValidCapacity(response.Value.AppCapacity) ||
                response.Value.Handlers is null ||
                response.Value.Handlers.Any(handler =>
                    string.IsNullOrWhiteSpace(handler.HandlerName) ||
                    !IsValidCapacity(handler.Capacity)))
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

    private async Task<bool> ScaleAsync(
        FunctionScaleTarget target,
        FunctionRevisionInKubernetes subject,
        RevisionState state,
        int replicas,
        CancellationToken cancellationToken)
    {
        var result = await kubernetesManager.ScaleAsync(subject, replicas, cancellationToken);
        if (result is IFailure failure)
        {
            logger.LogWarning(
                "[function-scaler-scale-failed] Failed to scale Function revision {RevisionId} to {Replicas}: {Message}",
                target.Revision.Id,
                replicas,
                failure.Message);
            return false;
        }

        state.LastRequestedReplicas = replicas;
        logger.LogInformation(
            "[function-scaler-scaled] Scaled Function revision {RevisionId} to {Replicas}",
            target.Revision.Id,
            replicas);
        return true;
    }

    private static int DivideRoundUp(int value, int divisor) => checked((value + divisor - 1) / divisor);
    private static bool IsFullyReady(FunctionReplicaState replicas, int readyEndpoints) =>
        replicas.GenerationObserved &&
        replicas.Desired == replicas.Current &&
        replicas.Current == replicas.Ready &&
        replicas.Ready == replicas.Available &&
        replicas.Available == readyEndpoints;

    private static bool HasConverged(FunctionReplicaState? replicas, int readyEndpoints, int expected) =>
        replicas is not null &&
        replicas.Desired == expected &&
        IsFullyReady(replicas, readyEndpoints);

    private static bool IsValidCapacity(FunctionCapacityStatus capacity) =>
        capacity is { Limit: > 0, Reserved: >= 0, Started: >= 0, Available: >= 0 } &&
        capacity.Reserved + capacity.Started <= capacity.Limit &&
        capacity.Available == capacity.Limit - capacity.Reserved - capacity.Started;

    private static string Key(FunctionScaleTarget target) =>
        $"{target.App.TenantId}/{target.App.Id}/{target.Revision.Id}";

    private static string PendingKey(FunctionScaleTarget target) => Key(target);

    public void Dispose()
    {
        foreach (var state in states.Values)
            state.Dispose();
        states.Clear();
    }
}

public sealed class FunctionScalerWorker(
    IFunctionScaler scaler,
    FunctionScalerOptions options,
    TimeProvider timeProvider,
    ILogger<FunctionScalerWorker> logger) : BackgroundService
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
