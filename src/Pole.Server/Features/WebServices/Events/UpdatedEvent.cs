using MassTransit;
using Pole.Server.Features.Common.Abstractions;

namespace Pole.Server.Features.WebServices.Events;

[EntityName("web-service.updated")]
public sealed record UpdatedEvent(string WebServiceId) : BaseEvent;