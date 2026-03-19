using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Events.WebServices;

[EntityName("create-web-service-requested")]
public record CreateEvent(
    string WebServiceId
) : BaseEvent;
