// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson.Serialization.Attributes;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.GitOps.Domain;

public sealed record GitOperationReceipt(
    [property: BsonId] string Id,
    string TenantId,
    string SourceId,
    string RunId,
    long Fence,
    string Kind,
    string Name,
    string Phase,
    string ResourceId,
    string DesiredHash,
    string? Blocker,
    bool Changed,
    long PlanRevision,
    GitLifecycleEffect? Lifecycle
) : ITenantOwned;
