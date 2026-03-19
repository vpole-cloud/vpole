using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Events.WebServices;

[EntityName("update-web-service")]
public sealed record UpdateEvent : BaseEvent
{
    public required string WebServiceId { get; init; } 
}