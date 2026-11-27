// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson.Serialization.Attributes;
using Pole.Features.GitOps.Primitives;
using Pole.Multitenancy.Primitives;

namespace Pole.Features.GitOps.Domain;

public sealed record ManagedDeclaration(
    [property: BsonId] string Id,
    string TenantId,
    string Kind,
    string Name,
    string SourceId,
    string SourceUrl,
    string ResourceId,
    string DesiredHash,
    string? AppliedHash,
    string? Blocker,
    bool PendingRemoval,
    string LastRunId,
    string LastPhase,
    long MutationFence,
    GitResourceMaterialization Materialization,
    GitManagedProjections Projections
) : ITenantOwned;
