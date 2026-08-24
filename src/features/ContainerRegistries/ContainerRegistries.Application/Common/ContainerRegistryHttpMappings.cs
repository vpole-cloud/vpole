using System.Net;
using Pole.ASP.Helpers;
using Pole.Features.ContainerRegistries.Application.Security;

namespace Pole.Features.ContainerRegistries.Application.Common;

public static class ContainerRegistryHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<RegistryCredentialRewrapRejected>(MapRewrapRejected),
            HttpProblemMapping.For<RegistryCredentialRewrapFailed>(MapRewrapFailed)
        ]);

    private static HttpProblem MapRewrapRejected(RegistryCredentialRewrapRejected _) =>
        new(
            (int)HttpStatusCode.Conflict,
            PoleProblemTypeId.FromCode("container-registries.credentials.rewrap-rejected"),
            "Container registry credentials could not be rewrapped",
            "container-registries.credentials.rewrap-rejected");

    private static HttpProblem MapRewrapFailed(RegistryCredentialRewrapFailed _) =>
        new(
            (int)HttpStatusCode.InternalServerError,
            PoleProblemTypeId.FromCode("container-registries.credentials.rewrap-failed"),
            "Container registry credentials could not be rewrapped",
            "container-registries.credentials.rewrap-failed");
}
