// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Collections.Immutable;
using Pole.Features.Functions.Domain;
using Pole.Features.GitOps.Contracts;

namespace Pole.Features.Functions.Application.Common;

public sealed record FunctionAppDto(
    string Id,
    string TenantId,
    string ResourceGroupId,
    FunctionPlacementDto Placement,
    string Name,
    string FunctionRoute,
    string? ActiveRevisionId,
    string? PendingRevisionId,
    int NextRevisionNumber,
    FunctionAppDesiredState DesiredState,
    FunctionAppStatus Status,
    FunctionQuotaAllocation AdmittedQuota,
    ImmutableHashSet<string> Tags,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    GitResourceStatus? GitOps
);

public sealed record FunctionPlacementDto(
    string ResourceDeploymentId,
    string RuntimeCellId,
    string DeploymentTargetId
);

public sealed record FunctionRevisionDto(
    string Id,
    string FunctionAppId,
    int RevisionNumber,
    FunctionRevisionSpec Spec,
    FunctionRevisionStatus Status,
    DateTimeOffset CreatedAtUtc
);

public sealed record FunctionAppDetailsDto(
    FunctionAppDto FunctionApp,
    FunctionRevisionDto? ActiveRevision,
    FunctionRevisionDto? PendingRevision
);

public static class FunctionAppDtoExtensions
{
    public static FunctionAppDto ToDto(this FunctionApp app) => new(
        app.Id,
        app.TenantId,
        app.ResourceGroupId,
        new FunctionPlacementDto(
            app.Placement.ResourceDeploymentId,
            app.Placement.RuntimeCellId,
            app.Placement.DeploymentTargetId),
        app.Name,
        app.FunctionRoute,
        app.ActiveRevisionId,
        app.PendingRevisionId,
        app.NextRevisionNumber,
        app.DesiredState,
        app.Status,
        app.AdmittedQuota,
        app.Tags,
        app.CreatedAtUtc,
        app.UpdatedAtUtc,
        GitOps: null);

    public static FunctionRevisionDto ToDto(this FunctionRevision revision) => new(
        revision.Id,
        revision.FunctionAppId,
        revision.RevisionNumber,
        revision.Spec,
        revision.Status,
        revision.CreatedAtUtc);
}
