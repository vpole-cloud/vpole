using MongoDB.Driver;
using Pole.Features.Functions.Application.Common;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Contracts;
using Pole.Features.ResourceConnections.Contracts;
using Pole.Features.ResourceDeployments.Contracts;
using Pole.Features.Secrets.Contracts;

namespace Pole.Features.Functions.Application.Lifecycle;

public sealed record FunctionAppDeletionApplied;

public sealed record FunctionSecretBindingRemovalRequired(
    string FunctionAppId
);

public union ApplyFunctionAppDeletionOutcome(
    FunctionAppDeletionApplied,
    FunctionSecretBindingRemovalRequired,
    FunctionAppDeletionChanged,
    FunctionAppGitOwnedConfiguration,
    ResourceDeploymentMutationError);

public sealed record ApplyFunctionAppDeletionRequest(
    FunctionApp Current,
    ResourceDeploymentExactMutationRequest Deployment,
    GitLifecycleAuthority? Lifecycle = null,
    IReadOnlyList<ResourceConnectionPlanningVersion>? ReviewedConnections = null
);

public delegate Task<ApplyFunctionAppDeletionOutcome> ApplyFunctionAppDeletion(
    ApplyFunctionAppDeletionRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

// Quota admission/release is owned by the caller in this same transaction.
internal sealed class FunctionAppDeletionWorkflow(
    BeginFunctionAppDeletion beginDeletion,
    RequestExactResourceDeploymentDeletion deleteDeployment,
    TimeProvider clock,
    EnsureConsumerSecretBindingRemoval ensureBindingsRemovable
)
{
    public async Task<ApplyFunctionAppDeletionOutcome> ExecuteAsync(
        ApplyFunctionAppDeletionRequest request,
        IClientSessionHandle session,
        CancellationToken cancellationToken)
    {
        var current = request.Current;
        var bindings = await ensureBindingsRemovable(
            new EnsureConsumerSecretBindingRemovalRequest(
                current.TenantId,
                FunctionResourceDeployment.ResourceType,
                current.Id),
            session,
            cancellationToken);
        if (bindings is ConsumerSecretBindingsRequireRemoval)
            return new FunctionSecretBindingRemovalRequired(current.Id);
        if (bindings is ConsumerSecretBindingDeletionChanged)
            return new FunctionAppDeletionChanged(current.Id);

        var started = await beginDeletion(
            new BeginFunctionAppDeletionRequest(
                request.Current,
                clock.GetUtcNow(),
                request.Lifecycle,
                request.ReviewedConnections),
            session,
            cancellationToken);
        if (started is FunctionAppDeletionChanged changed)
            return changed;
        if (started is FunctionAppGitOwnedConfiguration owned)
            return owned;

        var deleted = await deleteDeployment(request.Deployment, session, cancellationToken);
        return deleted switch
        {
            PlacementSummaryDto => new FunctionAppDeletionApplied(),
            ResourceDeploymentMutationError rejected => rejected,
        };
    }
}
