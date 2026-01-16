
using MassTransit;
using Pole.Server.Features.Common.Abstractions;

namespace Pole.Server.Features.WebServices.Events;

[EntityName("update-web-service")]
public sealed record UpdateEvent : BaseEvent
{
    public required string WebServiceId { get; init; } 
}