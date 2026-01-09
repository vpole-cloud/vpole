
namespace Pole.Server.Events.WebServices;

public sealed record UpdateWebServiceRequestedEvent : BaseEvent
{
    public required string WebServiceId { get; init; } 
}