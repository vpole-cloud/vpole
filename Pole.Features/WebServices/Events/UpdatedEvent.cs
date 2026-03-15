using MassTransit;
using Pole.Features.Core.Abstractions;

namespace Pole.Features.WebServices.Events;

[EntityName("web-service.updated")]
public sealed record UpdatedEvent(string WebServiceId) : BaseEvent;