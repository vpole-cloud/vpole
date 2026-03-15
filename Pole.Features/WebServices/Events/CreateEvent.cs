using MassTransit;
using Pole.Features.Core.Abstractions;

namespace Pole.Features.WebServices.Events;

[EntityName("create-web-service-requested")]
public record CreateEvent(
    string WebServiceId
) : BaseEvent;
