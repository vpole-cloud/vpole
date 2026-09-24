using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.StorageProfiles.Application.Discovery;
using Pole.Features.StorageProfiles.Contracts;

namespace Pole.Features.StorageProfiles.Application.Common;

public static class StorageProfileHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<StorageProfileResolutionError>(MapResolutionError)
        ]);

    public static HttpProblem MapResolutionError(
        StorageProfileResolutionError error) => error switch
        {
            StorageProfileNotFound notFound => MapNotFound(notFound),
            StorageProfileDisabled disabled => Problem(
                HttpStatusCode.UnprocessableEntity,
                "storage-profiles.disabled",
                "Storage profile is disabled",
                ("storageProfileId", disabled.StorageProfileId)),
            StorageProfileTenantAccessDenied denied => Problem(
                HttpStatusCode.UnprocessableEntity,
                "storage-profiles.tenant-access-denied",
                "Storage profile is unavailable to this tenant",
                ("storageProfileId", denied.StorageProfileId)),
            StorageProfileResourceTypeNotAllowed unavailable => Problem(
                HttpStatusCode.UnprocessableEntity,
                "storage-profiles.resource-type-not-allowed",
                "Storage profile is unavailable for this resource type",
                ("storageProfileId", unavailable.StorageProfileId),
                ("resourceType", unavailable.ResourceType.ToString())),
            StorageProfileSizeNotAllowed size => Problem(
                HttpStatusCode.UnprocessableEntity,
                "storage-profiles.size-not-allowed",
                "Requested storage size is outside the profile limits",
                ("storageProfileId", size.StorageProfileId),
                ("sizeGb", size.SizeGb)),
            StorageProfileDeploymentTargetNotConfigured mapping => Problem(
                HttpStatusCode.UnprocessableEntity,
                "storage-profiles.deployment-target-not-configured",
                "Storage profile is not configured for the selected deployment target",
                ("storageProfileId", mapping.StorageProfileId),
                ("deploymentTargetId", mapping.DeploymentTargetId)),
            StorageProfileStorageClassUnavailable storageClass => Problem(
                HttpStatusCode.UnprocessableEntity,
                "storage-profiles.storage-class-unavailable",
                "Configured Kubernetes storage class is unavailable",
                ("storageProfileId", storageClass.StorageProfileId),
                ("deploymentTargetId", storageClass.DeploymentTargetId),
                ("storageClassName", storageClass.StorageClassName)),
            StorageProfileDiscoveryUnavailable unavailable => Problem(
                HttpStatusCode.ServiceUnavailable,
                "storage-profiles.discovery-unavailable",
                "Storage class discovery is unavailable",
                ("deploymentTargetId", unavailable.DeploymentTargetId))
        };

    public static HttpProblem MapTargetMappingsInvalid(StorageProfileTargetMappingsInvalid invalid) => Problem(
        HttpStatusCode.UnprocessableEntity,
        "storage-profiles.target-mappings.invalid",
        "Storage profile target mappings are invalid",
        ("errors", invalid.Errors));

    public static HttpProblem MapNotFound(StorageProfileNotFound notFound) => Problem(
        HttpStatusCode.NotFound,
        "storage-profiles.not-found",
        "Storage profile not found",
        ("storageProfileId", notFound.StorageProfileId));

    public static HttpProblem MapRevisionConflict(StorageProfileRevisionConflict conflict) =>
        Problem(
            HttpStatusCode.Conflict,
            "storage-profiles.revision-conflict",
            "Storage profile changed",
            ("storageProfileId", conflict.StorageProfileId));

    public static HttpProblem MapDiscoveryUnavailable(
        KubernetesStorageDiscoveryUnavailable unavailable) =>
        Problem(
            HttpStatusCode.ServiceUnavailable,
            "storage-profiles.discovery-unavailable",
            "Storage discovery is unavailable",
            ("deploymentTargetId", unavailable.DeploymentTargetId));

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
