using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;

namespace Pole.Features.Secrets.Application.Values;

public delegate Task StoreSecretChange(
    IClientSessionHandle session,
    Secret secret,
    SecretChange change,
    CancellationToken ct);

internal sealed class OutboxStoreSecretChange(
    StoreOutboxWrites outbox
)
{
    public Task InvokeAsync(
        IClientSessionHandle session,
        Secret secret,
        SecretChange change,
        CancellationToken ct) => outbox(
        session,
        [
            OutboxWrite.Create(
                new SecretChangedEvent(
                    secret.Id,
                    secret.CurrentVersion,
                    secret.Revision,
                    change,
                    secret.Name,
                    EventCore.Create(secret.TenantId))),
        ],
        ct);
}
