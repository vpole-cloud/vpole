using MassTransit;
using Pole.Server.Features.Common.Abstractions;

namespace Pole.Server.Features.WebServices.Events;

[EntityName("create-web-service-requested")]
public record CreateEvent : BaseEvent
{
    public required string WebServiceId { get; init; }
}