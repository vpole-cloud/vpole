using System.Collections.Immutable;
using MassTransit;
using Pole.Domain.Models;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.WebServices.Events;

[EntityName("update-web-service")]
public sealed record UpdateEvent(
    string WebServiceId,
    UpdateEventChanges Changes
    ) : BaseEvent;

public sealed record UpdateEventChanges(
    UpdateLog<WebServiceSpec> WebServiceSpec,
    UpdateLog<ImmutableHashSet<string>> Tags);