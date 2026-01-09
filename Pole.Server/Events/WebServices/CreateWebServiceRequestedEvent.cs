namespace Pole.Server.Events.WebServices;

public record CreateWebServiceRequestedEvent : BaseEvent
{
    public required string WebServiceId { get; init; }
}