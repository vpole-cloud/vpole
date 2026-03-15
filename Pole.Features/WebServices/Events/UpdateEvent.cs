using MassTransit;
using Pole.Features.Core.Abstractions;

namespace Pole.Features.WebServices.Events;

[EntityName("update-web-service")]
public sealed record UpdateEvent : BaseEvent
{
    public required string WebServiceId { get; init; } 
}