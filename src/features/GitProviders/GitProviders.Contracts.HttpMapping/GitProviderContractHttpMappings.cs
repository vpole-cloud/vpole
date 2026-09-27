using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.GitProviders.Contracts.HttpMapping;

public static class GitProviderContractHttpMappings
{
    public static HttpProblem MapSourceRepositoryBuildNotFound(
        SourceRepositoryBuildNotFound notFound) =>
        new(
            (int)HttpStatusCode.NotFound,
            PoleProblemTypeId.FromCode("git-providers.source-repositories.not-found"),
            "Source repository not found",
            "git-providers.source-repositories.not-found",
            extensions: new Dictionary<string, object>
            {
                ["sourceRepositoryId"] = notFound.SourceRepositoryId
            });
}
