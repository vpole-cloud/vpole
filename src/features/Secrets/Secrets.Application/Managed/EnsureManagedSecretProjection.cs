using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Contracts;
using Pole.Features.Secrets.Domain;

namespace Pole.Features.Secrets.Application.Managed;

internal sealed class ReconcileManagedSecretProjection(
    LoadManagedSecret loadSecret,
    LoadSecretProjections loadProjections,
    SecretProjectionReconciler reconcile
)
{
    public async Task<EnsureManagedSecretProjectionOutcome> InvokeAsync(
        EnsureManagedSecretProjectionRequest request,
        CancellationToken ct)
    {
        var secret = await loadSecret(request.TenantId, request.Owner, ct);
        if (secret is null)
            return new ManagedSecretUnavailable();
        await reconcile.ReconcileAsync(secret.TenantId, secret.Id, ct);
        var latest = await loadSecret(request.TenantId, request.Owner, ct);
        if (latest is null || latest.Id != secret.Id)
            return new ManagedSecretUnavailable();
        var projection
            = (await loadProjections(secret.TenantId, secret.Id, ct)).SingleOrDefault(x =>
                x.DeploymentId == request.DeploymentId);
        var management = (ProviderManagedSecret)latest.Management;
        return projection is { State: ReadySecretProjection } && projection.ConsumerType == management.ResourceType &&
            projection.ConsumerId == management.ResourceId && projection.BindingId == management.BindingId &&
            projection.DesiredRevision == latest.Revision && projection.AppliedVersion == latest.CurrentVersion
                ? new SecretProjectionReference(secret.Id, projection.KubernetesName, latest.CurrentVersion)
                : new ManagedSecretUnavailable();
    }
}
