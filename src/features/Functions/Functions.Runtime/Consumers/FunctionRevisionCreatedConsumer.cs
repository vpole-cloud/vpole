using MassTransit;
using Microsoft.Extensions.Logging;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Application.Persistence.Lifecycle;
using Pole.Features.Functions.Application.Persistence.Reads;
using Pole.Features.Functions.Application.Persistence.Runtime.Provisioning;
using Pole.Features.Functions.Domain;
using Pole.Features.Functions.Events;
using Pole.Features.Functions.Runtime.Kubernetes;
using Pole.Features.Functions.Runtime.Provisioning;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.ResourceDeployments.Events;

namespace Pole.Features.Functions.Runtime.Consumers;

public sealed class FunctionRevisionCreatedConsumer(
    LoadFunctionRevisionProvisioningSubject loadProvisioningSubject,
    LoadFunctionAppById loadFunctionAppById,
    LoadExactResourceDeploymentRuntimePlacement loadDeploymentPlacement,
    IFunctionResourcePlacementResolver placementResolver,
    IFunctionRevisionKubernetesManager kubernetesManager,
    FailFunctionRevision failFunctionRevision,
    IEnumerable<IFunctionRevisionProvisioner> provisioners,
    ILogger<FunctionRevisionCreatedConsumer> logger)
    : IConsumer<FunctionRevisionCreatedEvent>,
      IConsumer<ResourceDeploymentAppliedEvent>
{
    public Task Consume(ConsumeContext<FunctionRevisionCreatedEvent> context) =>
        HandleAsync(context.Message, context.CancellationToken);

    public Task Consume(ConsumeContext<ResourceDeploymentAppliedEvent> context) =>
        HandleAppliedAsync(context.Message, context.CancellationToken);

    public async Task HandleAsync(
        FunctionRevisionCreatedEvent message,
        CancellationToken cancellationToken = default)
        => await HandleRevisionAsync(
            message.EventCore.TenantId,
            message.FunctionAppId,
            message.RevisionId,
            message.Placement,
            cancellationToken);

    public async Task HandleAppliedAsync(
        ResourceDeploymentAppliedEvent message,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(
                message.ResourceType,
                FunctionResourceDeployment.ResourceType.Value,
                StringComparison.Ordinal))
            return;

        var tenantId = message.EventCore.TenantId;
        var app = await loadFunctionAppById(tenantId, message.ResourceId, cancellationToken);
        if (app is null || app.DesiredState == FunctionAppDesiredState.Deleting ||
            app.PendingRevisionId is not { } pendingRevisionId ||
            !string.Equals(app.Placement.ResourceDeploymentId, message.ResourceDeploymentId, StringComparison.Ordinal) ||
            !string.Equals(app.Placement.RuntimeCellId, message.RuntimeCellId, StringComparison.Ordinal))
            return;

        var deployment = await loadDeploymentPlacement(
            tenantId,
            FunctionResourceDeployment.ResourceType,
            app.Id,
            message.ResourceDeploymentId,
            cancellationToken);
        if (deployment is null ||
            !string.Equals(deployment.Status, "Applied", StringComparison.Ordinal) ||
            !string.Equals(deployment.DesiredOperation, "Apply", StringComparison.Ordinal) ||
            deployment.DesiredGeneration != message.Generation ||
            !string.Equals(deployment.RuntimeCellId, message.RuntimeCellId, StringComparison.Ordinal) ||
            !string.Equals(deployment.DeploymentTargetId, app.Placement.DeploymentTargetId, StringComparison.Ordinal))
            return;

        await HandleRevisionAsync(tenantId, app.Id, pendingRevisionId, null, cancellationToken);
    }

    private async Task HandleRevisionAsync(
        string tenantId,
        string functionAppId,
        string revisionId,
        FunctionPlacementSnapshot? expectedPlacement,
        CancellationToken cancellationToken)
    {
        var subject = await loadProvisioningSubject(
            tenantId,
            functionAppId,
            revisionId,
            cancellationToken);
        if (subject is null)
            throw new InvalidOperationException("Function App or revision no longer exists.");
        var app = subject.App;
        var revision = subject.Revision;
        if (revision.Status is not (ProvisioningFunctionRevisionStatus or ReadyFunctionRevisionStatus) ||
            app.DesiredState == FunctionAppDesiredState.Deleting ||
            !string.Equals(app.PendingRevisionId, revision.Id, StringComparison.Ordinal))
            return;

        if (expectedPlacement is not null && !Matches(expectedPlacement, app.Placement))
            throw new InvalidOperationException("Function revision event placement does not match the Function App.");
        var placementResult = await placementResolver.ResolveAsync(app, cancellationToken: cancellationToken);
        if (placementResult is FunctionResourcePlacementUnavailable)
            throw new InvalidOperationException("Function resource deployment placement is not ready.");
        var placement = (FunctionResourcePlacement)placementResult.Value;
        var kubernetesRevision = new FunctionRevisionInKubernetes(app, revision, placement);

        if (revision.Status is ProvisioningFunctionRevisionStatus)
        {
            var applied = await kubernetesManager.ApplyAsync(
                kubernetesRevision,
                cancellationToken);
            if (applied is FunctionRevisionKubernetesOperationError applyFailure)
            {
                if (applyFailure.Value is FunctionRevisionGatewaySourceUnavailable)
                {
                    logger.LogWarning(
                        "[function-revision-provisioning-deferred] Gateway source is unavailable for revision {RevisionId} of Function App {FunctionAppId}: {Reason}",
                        revision.Id,
                        app.Id,
                        applyFailure.Message());
                    return;
                }
                logger.LogError(
                    "[function-revision-provisioning-failed] Kubernetes could not apply revision {RevisionId} for Function App {FunctionAppId}: {Reason}",
                    revision.Id,
                    app.Id,
                    applyFailure.Message());
                await FailAsync(
                    tenantId,
                    app,
                    revision,
                    "kubernetes-apply",
                    applyFailure.Message(),
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
                    kubernetesRevision,
                    0,
                    cancellationToken);
                if (stopped is FunctionRevisionKubernetesOperationError stopFailure)
                {
                    logger.LogWarning(
                        "[function-revision-stop-failed] Could not return unconfigured ready revision {RevisionId} to zero replicas: {Reason}",
                        revision.Id,
                        stopFailure.Message());
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

        var outcome = await provisioner.ProvisionAsync(kubernetesRevision, cancellationToken);
        await (outcome switch
        {
            FunctionRevisionProvisioningCompleted => Task.CompletedTask,
            FunctionRevisionProvisioningDeferred deferred => DeferProvisioningAsync(app, revision, deferred),
            FunctionRevisionProvisioningFailed failed => FailAsync(
                tenantId,
                app,
                revision,
                failed.Code,
                failed.Message,
                cancellationToken)
        });
    }

    private Task DeferProvisioningAsync(
        FunctionApp app,
        FunctionRevision revision,
        FunctionRevisionProvisioningDeferred deferred)
    {
        logger.LogWarning(
            "[function-revision-provisioning-deferred] Revision {RevisionId} of Function App {FunctionAppId}: {Reason}",
            revision.Id,
            app.Id,
            deferred.Reason);
        return Task.CompletedTask;
    }

    private static bool Matches(
        FunctionPlacementSnapshot snapshot,
        FunctionPlacementIdentity placement) =>
        string.Equals(
            snapshot.ResourceDeploymentId,
            placement.ResourceDeploymentId,
            StringComparison.Ordinal) &&
        string.Equals(
            snapshot.RuntimeCellId,
            placement.RuntimeCellId,
            StringComparison.Ordinal) &&
        string.Equals(
            snapshot.DeploymentTargetId,
            placement.DeploymentTargetId,
            StringComparison.Ordinal);

    private async Task FailAsync(
        string tenantId,
        FunctionApp app,
        FunctionRevision revision,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        var failed = await failFunctionRevision(
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
