using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.ComputeProfiles.Contracts;
using Pole.Features.ComputeProfiles.Application.Persistence;
using Pole.Features.ComputeProfiles.Application.Persistence.Mutations;

namespace Pole.Features.ComputeProfiles.Application.Common;

public static class ComputeProfileHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<ComputeProfileMutationValidationError>(
                MapMutationValidationError),
            HttpProblemMapping.For<ComputeProfileRevisionConflict>(
                MapRevisionConflict),
            HttpProblemMapping.For<DefaultComputeProfileCannotBeDemoted>(
                MapDefaultDemotionRejected),
            HttpProblemMapping.For<DefaultComputeProfileCannotBeDeleted>(
                MapDefaultDeletionRejected)
        ]);

    private static HttpProblem MapMutationValidationError(
        ComputeProfileMutationValidationError error) => error switch
    {
        SelectedComputeProfileTenantsNotFound missing => Problem(
            HttpStatusCode.UnprocessableEntity,
            "compute-profiles.tenant-access.tenants-not-found",
            "One or more selected tenants do not exist",
            ("errors", new Dictionary<string, string[]>
            {
                ["TenantAccess.TenantIds"] =
                    ["One or more selected tenants do not exist."]
            }),
            ("tenantIds", missing.TenantIds)),
        ComputeProfileTargetMappingsInvalid invalid => Problem(
            HttpStatusCode.UnprocessableEntity,
            "compute-profiles.target-mappings.invalid",
            "Compute profile target mappings are invalid",
            ("errors", invalid.Errors))
    };

    private static HttpProblem MapRevisionConflict(
        ComputeProfileRevisionConflict conflict) => Problem(
        HttpStatusCode.Conflict,
        "compute-profiles.revision-conflict",
        "Compute profile changed",
        ("computeProfileId", conflict.ComputeProfileId));

    private static HttpProblem MapDefaultDemotionRejected(
        DefaultComputeProfileCannotBeDemoted rejected) => Problem(
        HttpStatusCode.Conflict,
        "compute-profiles.default-demotion-forbidden",
        "Promote another compute profile before demoting the current default",
        ("computeProfileId", rejected.ComputeProfileId));

    private static HttpProblem MapDefaultDeletionRejected(
        DefaultComputeProfileCannotBeDeleted rejected) => Problem(
        HttpStatusCode.Conflict,
        "compute-profiles.default-delete-forbidden",
        "The default compute profile cannot be deleted",
        ("computeProfileId", rejected.ComputeProfileId));

    private static HttpProblem Problem(
        HttpStatusCode status,
        string code,
        string title,
        params (string Name, object Value)[] extensions) =>
        new(
            (int)status,
            PoleProblemTypeId.FromCode(code),
            title,
            code,
            extensions: extensions.ToDictionary(
                extension => extension.Name,
                extension => extension.Value));
}
