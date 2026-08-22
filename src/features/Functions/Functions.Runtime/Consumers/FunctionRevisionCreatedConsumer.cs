using MassTransit;
using Microsoft.Extensions.Logging;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence.Runtime.Provisioning;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Events;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Provisioning;
using Pole.ResultType;

namespace Pole.Features.Functions.Runtime.Consumers;

public sealed class FunctionRevisionCreatedConsumer(
    LoadFunctionRevisionProvisioningSubject loadProvisioningSubject,
    IFunctionRevisionKubernetesManager kubernetesManager,
    IFunctionRevisionLifecycleService lifecycleService,
    IEnumerable<IFunctionRevisionProvisioner> provisioners,
    ILogger<FunctionRevisionCreatedConsumer> logger)
    : IConsumer<FunctionRevisionCreatedEvent>
{
    public Task Consume(ConsumeContext<FunctionRevisionCreatedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public async Task HandleAsync(
        FunctionRevisionCreatedEvent message,
        CancellationToken cancellationToken = default)
    {
        var tenantId = message.EventCore.TenantId;
        var subject = await loadProvisioningSubject(
            tenantId,
            message.FunctionAppId,
            message.RevisionId,
            cancellationToken);
        if (subject is null)
            throw new InvalidOperationException("Function App or revision no longer exists.");
        var app = subject.App;
        var revision = subject.Revision;
        if (revision.Status is not (ProvisioningFunctionRevisionStatus or ReadyFunctionRevisionStatus))
            return;

        if (revision.Status is ProvisioningFunctionRevisionStatus)
        {
            var applied = await kubernetesManager.ApplyAsync(
                new FunctionRevisionInKubernetes(app, revision),
                cancellationToken);
            if (applied is IFailure applyFailure)
            {
                logger.LogError(
                    "[function-revision-provisioning-failed] Kubernetes could not apply revision {RevisionId} for Function App {FunctionAppId}: {Reason}",
                    revision.Id,
                    app.Id,
                    applyFailure.Message);
                await FailAsync(
                    tenantId,
                    app,
                    revision,
                    "kubernetes-apply",
                    applyFailure.Message,
                    cancellationToken);
                return;
            }
        }

        var provisioner = provisioners.SingleOrDefault();
        if (provisioner is null)
        {
            if (revision.Status is ReadyFunctionRevisionStatus)
            {
                var stopped = await kubernetesManager.ScaleProvisioningAsync(
                    new FunctionRevisionInKubernetes(app, revision),
                    0,
                    cancellationToken);
                if (stopped is IFailure stopFailure)
                {
                    logger.LogWarning(
                        "[function-revision-stop-failed] Could not return unconfigured ready revision {RevisionId} to zero replicas: {Reason}",
                        revision.Id,
                        stopFailure.Message);
                }
            }

            await FailAsync(
                tenantId,
                app,
                revision,
                "runtime-auth-unconfigured",
                "Function runtime authentication is not configured.",
                cancellationToken);
            return;
        }

        var outcome = await provisioner.ProvisionAsync(app, revision, cancellationToken);
        if (outcome.Succeeded)
            return;
        await FailAsync(
            tenantId,
            app,
            revision,
            outcome.FailureCode ?? "provisioning",
            outcome.FailureMessage ?? "Function revision provisioning failed.",
            cancellationToken);
    }

    private async Task FailAsync(
        string tenantId,
        FunctionApp app,
        FunctionRevision revision,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        var failed = await lifecycleService.FailAsync(
            tenantId,
            app.Id,
            revision.Id,
            new FunctionRevisionFailure(code, message),
            cancellationToken);
        if (failed is not FunctionRevisionLifecycleCompleted)
            throw new InvalidOperationException(failed.FailureMessage());
    }
}

public static class FunctionRuntimeFeature;
