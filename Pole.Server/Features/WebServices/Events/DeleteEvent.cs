using MassTransit;
using Pole.Server.Features.Common.Abstractions;

namespace Pole.Server.Features.WebServices.Events;

[EntityName("delete-web-service")]
public record DeleteEvent(
    string WebServiceId,
    string ResourceGroupId
) : BaseEvent;