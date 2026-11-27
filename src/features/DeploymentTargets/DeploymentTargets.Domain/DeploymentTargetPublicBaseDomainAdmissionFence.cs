// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Bson.Serialization.Attributes;

namespace Pole.Features.DeploymentTargets.Domain;

/// <summary>
///     Singleton write target used to serialize admission of public DNS suffixes. Equality has a
///     unique index, but ancestor/descendant overlap needs a concrete transaction conflict point.
/// </summary>
public sealed record DeploymentTargetPublicBaseDomainAdmissionFence(
    [property: BsonId] string Id,
    long Revision
)
{
    public const string SingletonId = "public-base-domain-admission";
    public const string CollectionName = "DeploymentTargetPublicBaseDomainAdmissionFences";
}
