using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Secrets.Events;

[EntityName("pole.secrets/v1/secret-changed")]
public sealed record SecretChangedEvent(
    string SecretId,
    long Version,
    long Revision,
    string Change,
    string? BindingId) : IPoleEvent
{
    public EventCore EventCore
    {
        get; init;
    } = EventCore.Create();
}
