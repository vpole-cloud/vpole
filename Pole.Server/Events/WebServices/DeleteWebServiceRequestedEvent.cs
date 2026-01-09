namespace Pole.Server.Events.WebServices;

public record DeleteWebServiceRequestedEvent(
    string WebServiceId,
    string ResourceGroupId
) : BaseEvent;