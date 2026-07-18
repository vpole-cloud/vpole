using System.Collections.Concurrent;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Coordination;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.ResultType;

namespace Pole.Features.Functions.Runtime.Activation;

public interface IFunctionRevisionActivator
{
    Task<Result<FunctionEndpointSnapshot>> EnsureActiveAsync(
        FunctionApp app,
        FunctionRevision revision,
        CancellationToken cancellationToken = default,
        bool forceScaleFence = false);
}

public sealed class FunctionRevisionActivator(
    IFunctionEndpointRegistry endpointRegistry,
    IFunctionActivationCoordinator coordinator,
    IFunctionRevisionKubernetesManager kubernetesManager,
    TimeProvider timeProvider) : IFunctionRevisionActivator
{
    private readonly ConcurrentDictionary<string, Task<Result<FunctionEndpointSnapshot>>> activations =
        new(StringComparer.Ordinal);

    public async Task<Result<FunctionEndpointSnapshot>> EnsureActiveAsync(
        FunctionApp app,
        FunctionRevision revision,
        CancellationToken cancellationToken = default,
        bool forceScaleFence = false)
    {
        var subject = new FunctionRevisionInKubernetes(app, revision);
        var service = new FunctionServiceKey(subject.Namespace, subject.ServiceName);
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
                var (owner, key, expected) = ((FunctionRevisionActivator, string, Task<Result<FunctionEndpointSnapshot>>))state!;
                owner.activations.TryRemove(new KeyValuePair<string, Task<Result<FunctionEndpointSnapshot>>>(key, expected));
            },
            (this, activationKey, activation),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await activation.WaitAsync(cancellationToken);
    }

    private async Task<Result<FunctionEndpointSnapshot>> ActivateCoreAsync(
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
                    return Failures.ExternalService(
                        "Function revision activation was rejected by the coordinator.",
                        "FunctionRuntime",
                        decision.Rejection.ToString());
                }

                if (decision.Kind == FunctionActivationBeginDecisionKind.Granted)
                {
                    if (decision.Lease is not { } lease)
                    {
                        return Failures.ExternalService(
                            "Function revision activation returned an invalid coordination lease.",
                            "FunctionRuntime",
                            string.Empty);
                    }

                    var scaled = await kubernetesManager.ScaleAsync(
                        subject,
                        lease.DesiredReplicas,
                        lease.Generation,
                        timeoutCancellation.Token);
                    if (scaled is Failure scaleFailure)
                        return scaleFailure;

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
        catch (Exception exception)
        {
            return Failures.ExternalService(
                "Function revision coordination is unavailable.",
                "FunctionRuntime",
                exception.Message);
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

    private static Failure ActivationTimeout(TimeSpan timeout) =>
        Failures.ExternalService(
            "Function revision did not become ready before the activation deadline.",
            "FunctionRuntime",
            $"Activation timed out after {timeout.TotalSeconds:0} seconds.");
}
