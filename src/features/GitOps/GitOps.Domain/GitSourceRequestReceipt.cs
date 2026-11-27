// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.GitOps.Domain;

public sealed record GitSourceRequestReceipt(
    [property: BsonId] string Id,
    string TenantId,
    string SourceId,
    string RequestKey,
    string InputHash,
    long Generation,
    GitRunInitiator Initiator,
    DateTime RequestedAtUtc
) : ITenantOwned;
