using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Contracts;
using Pole.Features.StorageProfiles.Contracts.HttpMapping;

namespace Pole.Features.StorageProfiles.Application.Common;

public static class StorageProfileHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } = StorageProfileContractHttpMappings.All;

    public static HttpProblem MapResolutionError(StorageProfileResolutionError error) =>
        StorageProfileContractHttpMappings.MapResolutionError(error);

    public static HttpProblem MapTargetMappingsInvalid(StorageProfileTargetMappingsInvalid invalid) => Problem(
        HttpStatusCode.UnprocessableEntity,
        "storage-profiles.target-mappings.invalid",
        "Storage profile target mappings are invalid",
        ("errors", invalid.Errors));

    public static HttpProblem MapNotFound(StorageProfileNotFound notFound) =>
        StorageProfileContractHttpMappings.MapNotFound(notFound);

    public static HttpProblem MapTechnicalNameConflict(StorageProfileTechnicalNameConflict conflict) => Problem(
        HttpStatusCode.Conflict,
        "storage-profiles.technical-name-conflict",
        "Storage profile technical name already exists",
        ("technicalName", conflict.TechnicalName));

    public static HttpProblem MapRevisionConflict(StorageProfileRevisionConflict conflict) => Problem(
        HttpStatusCode.Conflict,
        "storage-profiles.revision-conflict",
        "Storage profile changed",
        ("storageProfileId", conflict.StorageProfileId));

    public static HttpProblem MapDiscoveryUnavailable(KubernetesStorageDiscoveryUnavailable unavailable) => Problem(
        HttpStatusCode.ServiceUnavailable,
        "storage-profiles.discovery-unavailable",
        "Storage discovery is unavailable",
        ("deploymentTargetId", unavailable.DeploymentTargetId));

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
