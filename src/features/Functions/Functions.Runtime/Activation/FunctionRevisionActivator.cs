using System.Collections.Concurrent;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Coordination;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;

namespace Pole.Features.Functions.Runtime.Activation;

public interface IFunctionRevisionActivator
{
    Task<FunctionRevisionActivationOutcome> EnsureActiveAsync(
        FunctionRevisionInKubernetes subject,
        CancellationToken cancellationToken = default,
        bool forceScaleFence = false);
}

public union FunctionRevisionActivationOutcome(
    FunctionEndpointSnapshot,
    FunctionRevisionActivationError);

public sealed class FunctionRevisionActivator(
    IFunctionEndpointRegistry endpointRegistry,
    IFunctionActivationCoordinator coordinator,
    IFunctionRevisionKubernetesManager kubernetesManager,
    TimeProvider timeProvider) : IFunctionRevisionActivator
{
    private readonly ConcurrentDictionary<string, Task<FunctionRevisionActivationOutcome>> activations =
        new(StringComparer.Ordinal);

    public async Task<FunctionRevisionActivationOutcome> EnsureActiveAsync(
        FunctionRevisionInKubernetes subject,
        CancellationToken cancellationToken = default,
        bool forceScaleFence = false)
    {
        var app = subject.App;
        var revision = subject.Revision;
        var service = new FunctionServiceKey(
            subject.Placement.DeploymentTargetId,
            subject.Namespace,
            subject.ServiceName);
        var current = endpointRegistry.GetSnapshot(service);
        if (current.HasReadyEndpoints && !forceScaleFence)
            return current;

        var activationKey = $"{app.TenantId}/{app.Id}/{revision.Id}";
        var activation = activations.GetOrAdd(
            activationKey,
            _ => ActivateCoreAsync(
                new FunctionRevisionCoordinationKey(app.TenantId, app.Id, revision.Id),
                subject,
                service));
        _ = activation.ContinueWith(
            (_, state) =>
            {
                var (owner, key, expected) = ((FunctionRevisionActivator, string, Task<FunctionRevisionActivationOutcome>))state!;
                owner.activations.TryRemove(new KeyValuePair<string, Task<FunctionRevisionActivationOutcome>>(key, expected));
            },
            (this, activationKey, activation),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await activation.WaitAsync(cancellationToken);
    }

    private async Task<FunctionRevisionActivationOutcome> ActivateCoreAsync(
        FunctionRevisionCoordinationKey key,
        FunctionRevisionInKubernetes subject,
        FunctionServiceKey service)
    {
        var desiredReplicas = Math.Clamp(
            Math.Max(subject.Revision.Spec.Scaling.MinInstances, 1),
            1,
            subject.Revision.Spec.Scaling.MaxInstances);
        var timeout = TimeSpan.FromSeconds(subject.Revision.Spec.Scaling.ActivationTimeoutSeconds);
        using var timeoutCancellation = new CancellationTokenSource(timeout, timeProvider);
        var operationId = Guid.NewGuid().ToString("N");
        try
        {
            while (!timeoutCancellation.IsCancellationRequested)
            {
                var decision = await coordinator.BeginActivationAsync(
                    key,
                    new FunctionActivationBeginRequest(operationId, desiredReplicas),
                    timeoutCancellation.Token);
                if (decision.Kind == FunctionActivationBeginDecisionKind.Busy)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(Math.Clamp(decision.RetryAfterMilliseconds, 25, 250)),
                        timeProvider,
                        timeoutCancellation.Token);
                    continue;
                }
                if (decision.Kind == FunctionActivationBeginDecisionKind.Rejected)
                {
                    return new FunctionRevisionActivationError(
                        new FunctionActivationCoordinatorRejected(
                            "Function revision activation was rejected by the coordinator."));
                }

                if (decision.Kind == FunctionActivationBeginDecisionKind.Granted)
                {
                    if (decision.Lease is not { } lease)
                    {
                        return new FunctionRevisionActivationError(
                            new FunctionActivationCoordinatorUnavailable(
                                "Function revision activation returned an invalid coordination lease."));
                    }

                    var scaled = await kubernetesManager.ScaleAsync(
                        subject,
                        lease.DesiredReplicas,
                        lease.Generation,
                        timeoutCancellation.Token);
                    if (scaled is FunctionRevisionKubernetesOperationError scaleFailure)
                    {
                        return new FunctionRevisionActivationError(
                            new FunctionActivationScaleRejected(scaleFailure));
                    }

                    await coordinator.CompleteActivationAsync(
                        key,
                        new FunctionActivationCompleteRequest(
                            lease.LeaseId,
                            lease.Generation,
                            FunctionCoordinationCompletionOutcome.Applied),
                        timeoutCancellation.Token);
                }

                break;
            }
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
        {
            return ActivationTimeout(timeout);
        }
        catch (Exception)
        {
            return new FunctionRevisionActivationError(
                new FunctionActivationCoordinatorUnavailable(
                    "Function revision coordination is unavailable."));
        }

        var deadline = timeProvider.GetUtcNow() + timeout;
        while (timeProvider.GetUtcNow() < deadline)
        {
            var snapshot = endpointRegistry.GetSnapshot(service);
            if (snapshot.HasReadyEndpoints)
                return snapshot;
            try
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100),
                    timeProvider,
                    timeoutCancellation.Token);
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested)
            {
                return ActivationTimeout(timeout);
            }
        }

        return ActivationTimeout(timeout);
    }

    private static FunctionRevisionActivationError ActivationTimeout(TimeSpan timeout) =>
        new FunctionActivationTimedOut(
            $"Function revision did not become ready before the activation deadline ({timeout.TotalSeconds:0} seconds).");
}
