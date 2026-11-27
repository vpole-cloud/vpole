// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.GitProviders.Domain;

public sealed record GitHubDeliveryReceipt(
    [property: BsonId] string Id,
    string PayloadHash,
    string EventType,
    DateTime ReceivedAtUtc
);
