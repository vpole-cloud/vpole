using System.Security.Claims;
using MongoDB.Bson;
using Pole.ASP.Helpers;
using Pole.Features.Realtime.Contracts;
using Pole.Features.StorageProfiles.Application.Observation;
using Pole.Observation;

namespace Pole.Features.StorageProfiles.Application.Realtime;

public sealed class StorageObservationWakeup : ObservationWakeup;

public sealed class StorageObservationLifecycle(
    StorageObservationOptions options,
    StorageObservationContextLoader contexts,
    IStorageObservationStore store,
    StorageObservationWakeup wakeup
) : IRealtimeTopicLifecycle
{
    public string Topic => "storage-observations";

    public Task<bool> SubscribeAsync(
        RealtimeSubscription subscription,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken) => PutAsync(subscription, principal, false, cancellationToken);

    public Task<bool> RenewAsync(
        RealtimeSubscription subscription,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken) => PutAsync(subscription, principal, true, cancellationToken);

    public async Task UnsubscribeAsync(RealtimeSubscription subscription, CancellationToken cancellationToken)
    {
        if (Identity(subscription) is not { } identity)
            return;
        await store.RemoveDemandAsync(identity, cancellationToken);
        wakeup.Notify();
    }

    private async Task<bool> PutAsync(
        RealtimeSubscription subscription,
        ClaimsPrincipal principal,
        bool renew,
        CancellationToken ct)
    {
        if (Identity(subscription) is not { } identity)
            return false;
        // Generic realtime revalidates the ticket's platform role before invoking this owner.
        if (!options.Enabled || principal.Identity?.IsAuthenticated != true ||
            !principal.HasClaim(Claims.Roles, Roles.Admin))
        {
            await store.RemoveDemandAsync(identity, ct);
            wakeup.Notify();
            return false;
        }

        var context = await contexts.LoadAsync(identity.DeploymentTargetId, ct);
        if (context is null)
        {
            await store.RemoveDemandAsync(identity, ct);
            wakeup.Notify();
            return false;
        }

        var demand = new StorageObservationDemand(identity, subscription.ExpiresAtUtc);
        var outcome = renew
            ? await store.RenewDemandAsync(demand, context, ct)
            : await store.PutDemandAsync(demand, context, ct);
        wakeup.Notify();
        return outcome.Value is StorageObservationMutationAccepted &&
            await store.LoadFreshSnapshotAsync(context, ct) is not null;
    }

    private static StorageObservationDemandIdentity? Identity(RealtimeSubscription subscription) =>
        subscription.Request.Topic == "storage-observations" && subscription.Request.TenantId is null &&
        ObjectId.TryParse(subscription.Request.ResourceId, out var target) &&
        target.ToString() == subscription.Request.ResourceId
            ? new StorageObservationDemandIdentity(
                target.ToString(),
                subscription.Id,
                subscription.ConnectionId,
                subscription.SessionId)
            : null;
}
