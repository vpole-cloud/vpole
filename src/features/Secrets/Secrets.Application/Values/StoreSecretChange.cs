using MongoDB.Driver;
using Pole.Features.Events.Abstractions;
using Pole.Features.Outbox.Contracts;
using Pole.Features.Secrets.Application;
using Pole.Features.Secrets.Application.Bindings;
using Pole.Features.Secrets.Application.Bootstrap;
using Pole.Features.Secrets.Application.Declarations;
using Pole.Features.Secrets.Application.Lifecycle;
using Pole.Features.Secrets.Application.Managed;
using Pole.Features.Secrets.Application.Mongo;
using Pole.Features.Secrets.Application.Projections;
using Pole.Features.Secrets.Application.References;
using Pole.Features.Secrets.Application.Secrets;
using Pole.Features.Secrets.Domain;
using Pole.Features.Secrets.Events;

namespace Pole.Features.Secrets.Application.Values;

public delegate Task StoreSecretChange(
    IClientSessionHandle session,
    Secret secret,
    SecretChange change,
    CancellationToken ct);

internal sealed class OutboxStoreSecretChange(StoreOutboxWrites outbox)
{
    public Task InvokeAsync(
        IClientSessionHandle session,
        Secret secret,
        SecretChange change,
        CancellationToken ct) =>
        outbox(
            session,
            [
                OutboxWrite.Create(new SecretChangedEvent(
                    secret.Id,
                    secret.CurrentVersion,
                    secret.Revision,
                    change,
                    secret.Name,
                    EventCore.Create(secret.TenantId)))
            ],
            ct);
}
