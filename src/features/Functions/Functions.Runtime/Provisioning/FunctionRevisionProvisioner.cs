using Microsoft.Extensions.Logging;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence.Lifecycle;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Runtime.Activation;
using Pole.Features.Functions.Runtime.Agent;
using Pole.Features.Functions.Runtime.Endpoints;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Functions.Protocol;

namespace Pole.Features.Functions.Runtime.Provisioning;

public sealed record FunctionRevisionProvisioningCompleted;

public sealed record FunctionRevisionProvisioningDeferred(string Reason);

public sealed record FunctionRevisionProvisioningFailed(
    string Code,
    string Message);

public union FunctionRevisionProvisioningOutcome(
    FunctionRevisionProvisioningCompleted,
    FunctionRevisionProvisioningDeferred,
    FunctionRevisionProvisioningFailed);

public interface IFunctionRevisionProvisioner
{
    Task<FunctionRevisionProvisioningOutcome> ProvisionAsync(
        FunctionRevisionInKubernetes subject,
        CancellationToken cancellationToken);
}

public sealed class FunctionRevisionProvisioner(
    IFunctionRevisionActivator activator,
    IFunctionAgentClient agentClient,
    IFunctionRevisionKubernetesManager kubernetesManager,
    MarkFunctionRevisionReady markFunctionRevisionReady,
    PromoteFunctionRevision promoteFunctionRevision,
    ILogger<FunctionRevisionProvisioner> logger) : IFunctionRevisionProvisioner
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(10);

    public async Task<FunctionRevisionProvisioningOutcome> ProvisionAsync(
        FunctionRevisionInKubernetes subject,
        CancellationToken cancellationToken)
    {
        var app = subject.App;
        var revision = subject.Revision;
        if (revision.Status is ReadyFunctionRevisionStatus)
            return await PromoteAsync(subject, app, revision, cancellationToken);
        if (revision.Status is not ProvisioningFunctionRevisionStatus)
        {
            return new FunctionRevisionProvisioningFailed(
                "revision-status",
                "Only a provisioning or ready Function revision can be provisioned.");
        }

        var activation = await activator.EnsureActiveAsync(subject, cancellationToken);
        if (activation is FunctionRevisionActivationError activationFailure)
        {
            if (activationFailure.Value is FunctionActivationScaleRejected
                {
                    Error.Value: FunctionRevisionGatewaySourceUnavailable sourceFailure
                })
                return new FunctionRevisionProvisioningDeferred(sourceFailure.Message);
            return await FailAndStopAsync(subject, "activation", activationFailure.Message(), cancellationToken);
        }

        var snapshot = (FunctionEndpointSnapshot)activation.Value;

        var metadataFailure = "No ready Function Agent returned valid metadata.";
        foreach (var endpoint in snapshot.Endpoints)
        {
            try
            {
                using var metadataTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                metadataTimeout.CancelAfter(MetadataTimeout);
                var response = await agentClient.GetMetadataAsync(
                    endpoint,
                    app,
                    revision,
                    metadataTimeout.Token);
                if (!response.IsSuccess || response.Value is null)
                {
                    metadataFailure = response.Error?.Message ??
                                      $"Function Agent metadata endpoint returned {(int)response.StatusCode}.";
                    continue;
                }

                metadataFailure = ValidateMetadata(response.Value, endpoint, app, revision) ?? string.Empty;
                if (metadataFailure.Length == 0)
                {
                    var scaled = await kubernetesManager.ScaleProvisioningAsync(
                        subject,
                        revision.Spec.Scaling.MinInstances,
                        cancellationToken);
                    if (scaled is FunctionRevisionKubernetesOperationError scaleFailure)
                    {
                        if (scaleFailure.Value is FunctionRevisionGatewaySourceUnavailable sourceFailure)
                            return new FunctionRevisionProvisioningDeferred(sourceFailure.Message);
                        return await FailAndStopAsync(
                            subject,
                            "minimum-scale",
                            scaleFailure.Message(),
                            cancellationToken);
                    }

                    var ready = await markFunctionRevisionReady(
                        app.TenantId,
                        app.Id,
                        revision.Id,
                        cancellationToken);
                    if (ready is not FunctionRevisionLifecycleCompleted)
                    {
                        return await FailAndStopAsync(
                            subject,
                            "readiness",
                            ready.FailureMessage(),
                            cancellationToken);
                    }

                    return await PromoteAsync(subject, app, revision, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                metadataFailure = "Function Agent metadata request timed out.";
            }
            catch (Exception exception) when (exception is HttpRequestException or FunctionAgentProtocolException)
            {
                metadataFailure = exception.Message;
                logger.LogWarning(
                    exception,
                    "[function-agent-metadata-failed] Could not validate Agent endpoint {Address}:{Port} for revision {RevisionId}.",
                    endpoint.Address,
                    endpoint.Port,
                    revision.Id);
            }
        }

        return await FailAndStopAsync(subject, "agent-metadata", metadataFailure, cancellationToken);
    }

    private async Task<FunctionRevisionProvisioningOutcome> PromoteAsync(
        FunctionRevisionInKubernetes subject,
        FunctionApp app,
        FunctionRevision revision,
        CancellationToken cancellationToken)
    {
        var promoted = await promoteFunctionRevision(
            app.TenantId,
            app.Id,
            revision.Id,
            cancellationToken);
        return promoted is FunctionRevisionLifecycleCompleted
            ? new FunctionRevisionProvisioningCompleted()
            : await FailAndStopAsync(
                subject,
                "promotion",
                promoted.FailureMessage(),
                cancellationToken);
    }

    private async Task<FunctionRevisionProvisioningOutcome> FailAndStopAsync(
        FunctionRevisionInKubernetes subject,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        var stopped = await kubernetesManager.ScaleProvisioningAsync(subject, 0, cancellationToken);
        if (stopped is FunctionRevisionKubernetesOperationError stopFailure)
        {
            logger.LogWarning(
                "[function-revision-stop-failed] Could not return failed revision {RevisionId} to zero replicas: {Reason}",
                subject.Revision.Id,
                stopFailure.Message());
        }

        return new FunctionRevisionProvisioningFailed(code, message);
    }

    private static string? ValidateMetadata(
        FunctionAgentMetadataResponse metadata,
        FunctionEndpoint endpoint,
        FunctionApp app,
        FunctionRevision revision)
    {
        if (!string.Equals(metadata.ProtocolVersion, revision.Spec.RuntimeProtocolVersion, StringComparison.Ordinal))
            return "Function Agent protocol version does not match the revision.";
        if (!string.Equals(metadata.FunctionAppId, app.Id, StringComparison.Ordinal) ||
            !string.Equals(metadata.RevisionId, revision.Id, StringComparison.Ordinal))
            return "Function Agent runtime identity does not match the revision.";
        if (!string.Equals(metadata.AgentInstanceId, endpoint.TargetUid, StringComparison.Ordinal))
            return "Function Agent instance identity does not match its Kubernetes Pod UID.";
        if (metadata.LifecycleState != FunctionAgentLifecycleState.Accepting)
            return "Function Agent is not accepting invocations.";

        var expected = revision.Spec.Handlers.ToDictionary(handler => handler.Name, StringComparer.Ordinal);
        if (metadata.Handlers.Count != expected.Count ||
            metadata.Handlers.Select(handler => handler.Name).Distinct(StringComparer.Ordinal).Count() != expected.Count)
            return "Function Agent handler set does not match the revision.";
        foreach (var actual in metadata.Handlers)
        {
            if (!expected.TryGetValue(actual.Name, out var handler) ||
                actual.TriggerKind != TriggerKind(handler.Trigger) ||
                actual.MaxConcurrencyPerInstance != handler.MaxConcurrencyPerInstance)
                return $"Function Agent handler '{actual.Name}' does not match the revision.";
        }

        return null;
    }

    private static FunctionTriggerKind TriggerKind(FunctionTriggerSpec trigger) => trigger switch
    {
        HttpFunctionTriggerSpec => FunctionTriggerKind.Http,
        TimerFunctionTriggerSpec => FunctionTriggerKind.Timer,
        _ => throw new ArgumentOutOfRangeException(nameof(trigger), trigger, null)
    };
}
