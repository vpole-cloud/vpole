// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.PrivateAccess.Events;

[EntityName("pole.private-access/v1/grant-created")]
public sealed record PrivateAccessGrantCreatedEvent(
    string GrantId,
    string SubjectUserId,
    string ResourceDeploymentId,
    string EndpointId
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = null!;
}

[EntityName("pole.private-access/v1/grant-revoked")]
public sealed record PrivateAccessGrantRevokedEvent(
    string GrantId,
    long Revision
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = null!;
}

[EntityName("pole.private-access/v1/session-issued")]
public sealed record PrivateAccessSessionIssuedEvent(
    string GrantId,
    string SessionId,
    string SubjectUserId
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = null!;
}
