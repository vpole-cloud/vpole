using MassTransit;
using Pole.Features.Core.Abstractions;

namespace Pole.Features.WebServices.Events;

[EntityName("delete-web-service")]
public record DeleteEvent(
    string WebServiceId,
    string WebServiceName,
    string ResourceGroupId
) : BaseEvent;