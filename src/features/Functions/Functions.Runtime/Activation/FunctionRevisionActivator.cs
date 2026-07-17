using System.Collections.Concurrent;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.ResultType;

namespace Pole.Features.Functions.Runtime.Activation;

public interface IFunctionRevisionActivator
{
    Task<Result<FunctionEndpointSnapshot>> EnsureActiveAsync(
        FunctionApp app,
        FunctionRevision revision,
        CancellationToken cancellationToken = default);
}

public sealed class FunctionRevisionActivator(
    IFunctionEndpointRegistry endpointRegistry,
    IFunctionRevisionKubernetesManager kubernetesManager,
    TimeProvider timeProvider) : IFunctionRevisionActivator
{
    private readonly ConcurrentDictionary<string, Task<Result<FunctionEndpointSnapshot>>> activations =
        new(StringComparer.Ordinal);

    public async Task<Result<FunctionEndpointSnapshot>> EnsureActiveAsync(
        FunctionApp app,
        FunctionRevision revision,
        CancellationToken cancellationToken = default)
    {
        var subject = new FunctionRevisionInKubernetes(app, revision);
        var service = new FunctionServiceKey(subject.Namespace, subject.ServiceName);
        var current = endpointRegistry.GetSnapshot(service);
        if (current.HasReadyEndpoints)
            return current;

        var activationKey = $"{app.TenantId}/{app.Id}/{revision.Id}";
        var activation = activations.GetOrAdd(
            activationKey,
            _ => ActivateCoreAsync(subject, service));
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
        FunctionRevisionInKubernetes subject,
        FunctionServiceKey service)
    {
        var desiredReplicas = Math.Clamp(
            Math.Max(subject.Revision.Spec.Scaling.MinInstances, 1),
            1,
            subject.Revision.Spec.Scaling.MaxInstances);
        var scaled = await kubernetesManager.ScaleAsync(subject, desiredReplicas, CancellationToken.None);
        if (scaled is Failure scaleFailure)
            return scaleFailure;

        var timeout = TimeSpan.FromSeconds(subject.Revision.Spec.Scaling.ActivationTimeoutSeconds);
        var deadline = timeProvider.GetUtcNow() + timeout;
        while (timeProvider.GetUtcNow() < deadline)
        {
            var snapshot = endpointRegistry.GetSnapshot(service);
            if (snapshot.HasReadyEndpoints)
                return snapshot;
            await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider, CancellationToken.None);
        }

        return Failures.ExternalService(
            "Function revision did not become ready before the activation deadline.",
            "FunctionRuntime",
            $"Activation timed out after {timeout.TotalSeconds:0} seconds.");
    }
}
