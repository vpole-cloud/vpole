using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Events.WebServices;

[EntityName("web-service.updated")]
public sealed record UpdatedEvent(string WebServiceId) : BaseEvent;