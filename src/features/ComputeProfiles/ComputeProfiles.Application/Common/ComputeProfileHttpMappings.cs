// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Application.Catalog;

namespace Pole.Features.ComputeProfiles.Application.Common;

public static class ComputeProfileHttpMappings
{
    public static HttpProblem MapSelectedTenantsNotFound(SelectedComputeProfileTenantsNotFound missing) => Problem(
        HttpStatusCode.UnprocessableEntity,
        "compute-profiles.tenant-access.tenants-not-found",
        "One or more selected tenants do not exist",
        ("errors", new Dictionary<string, string[]>
        {
            ["TenantAccess.TenantIds"] = ["One or more selected tenants do not exist."],
        }),
        ("tenantIds", missing.TenantIds));

    public static HttpProblem MapTargetMappingsInvalid(ComputeProfileTargetMappingsInvalid invalid) => Problem(
        HttpStatusCode.UnprocessableEntity,
        "compute-profiles.target-mappings.invalid",
        "Compute profile target mappings are invalid",
        ("errors", invalid.Errors));

    public static HttpProblem MapNameConflict(ComputeProfileTechnicalNameConflict conflict) => Problem(
        HttpStatusCode.Conflict,
        "compute-profiles.technical-name-conflict",
        "The catalog technical name is already used");

    public static HttpProblem MapRevisionConflict(ComputeProfileRevisionConflict conflict) => Problem(
        HttpStatusCode.Conflict,
        "compute-profiles.revision-conflict",
        "Compute profile changed",
        ("computeProfileId", conflict.ComputeProfileId));

    public static HttpProblem MapDefaultDemotionRejected(DefaultComputeProfileCannotBeDemoted rejected) => Problem(
        HttpStatusCode.Conflict,
        "compute-profiles.default-demotion-forbidden",
        "Promote another compute profile before demoting the current default",
        ("computeProfileId", rejected.ComputeProfileId));

    public static HttpProblem MapDefaultDeletionRejected(DefaultComputeProfileCannotBeDeleted rejected) => Problem(
        HttpStatusCode.Conflict,
        "compute-profiles.default-delete-forbidden",
        "The default compute profile cannot be deleted",
        ("computeProfileId", rejected.ComputeProfileId));

    private static HttpProblem Problem(
        HttpStatusCode status,
        string code,
        string title,
        params (string Name, object Value)[] extensions) => new(
        (int)status,
        PoleProblemTypeId.FromCode(code),
        title,
        code,
        extensions: extensions.ToDictionary(extension => extension.Name, extension => extension.Value));
}
