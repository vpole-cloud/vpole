// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MassTransit;
using Pole.Features.Events.Abstractions;

namespace Pole.Features.Secrets.Events;

[EntityName("pole.secrets/v1/secret-projection-changed")]
public sealed record SecretProjectionChangedEvent(
    string SecretId,
    string ProjectionId,
    string ResourceGroupId,
    string BindingId,
    string DeploymentId,
    long DesiredVersion,
    long DesiredRevision,
    long StorageRevision
) : IPoleEvent
{
    public EventCore EventCore { get; init; } = EventCore.Create();
}
