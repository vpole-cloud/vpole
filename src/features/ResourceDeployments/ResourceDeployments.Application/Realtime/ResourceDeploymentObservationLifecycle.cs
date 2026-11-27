// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Security.Claims;
using MongoDB.Bson;
using Pole.Features.Realtime.Contracts;
using Pole.Features.ResourceDeployments.Application.Observation;

namespace Pole.Features.ResourceDeployments.Application.Realtime;

public sealed class ResourceDeploymentObservationLifecycle(
    ResourceDeploymentObservationOptions options,
    ResourceDeploymentObservationContextLoader contexts,
    IResourceDeploymentObservationStore store,
    ResourceDeploymentObservationWakeup wakeup
) : IRealtimeTopicLifecycle
{
    public string Topic => "runtime-observations";

    public Task<bool> SubscribeAsync(
        RealtimeSubscription subscription,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken) => PutAsync(subscription, false, cancellationToken);

    public Task<bool> RenewAsync(
        RealtimeSubscription subscription,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken) => PutAsync(subscription, true, cancellationToken);

    public async Task UnsubscribeAsync(RealtimeSubscription subscription, CancellationToken cancellationToken)
    {
        if (Identity(subscription) is not { } identity)
            return;
        await store.RemoveDemandAsync(identity, cancellationToken);
        wakeup.Notify();
    }

    private async Task<bool> PutAsync(
        RealtimeSubscription subscription,
        bool renew,
        CancellationToken ct)
    {
        if (!options.Enabled || Identity(subscription) is not { } identity)
            return false;
        var context = await contexts.LoadWatchAsync(
            new ObservationIdentity(identity.TenantId, identity.DeploymentId),
            ct);
        if (context is null)
        {
            await store.RemoveDemandAsync(identity, ct);
            wakeup.Notify();
            return false;
        }

        var demand = new ObservationDemand(identity, subscription.ExpiresAtUtc);
        var outcome = renew
            ? await store.RenewDemandAsync(demand, context, ct)
            : await store.PutDemandAsync(demand, context, ct);
        wakeup.Notify();
        return outcome.Value is ObservationMutationAccepted &&
            await store.LoadFreshSnapshotAsync(context, ct) is not null;
    }

    private static ObservationDemandIdentity? Identity(RealtimeSubscription subscription) =>
        ObjectId.TryParse(subscription.Request.TenantId, out var tenant) && ObjectId.TryParse(
            subscription.Request.ResourceId,
            out var deployment)
            ? new ObservationDemandIdentity(
                tenant.ToString(),
                deployment.ToString(),
                subscription.Id,
                subscription.ConnectionId,
                subscription.SessionId)
            : null;
}
