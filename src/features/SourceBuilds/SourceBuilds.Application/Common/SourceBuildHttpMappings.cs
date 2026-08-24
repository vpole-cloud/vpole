using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.SourceBuilds.Application.Common;

public static class SourceBuildHttpMappings
{
    public static IReadOnlyList<HttpProblemMapping> All { get; } =
        Array.AsReadOnly<HttpProblemMapping>(
        [
            HttpProblemMapping.For<SourceBuildConfigurationNotFound>(MapConfigurationNotFound),
            HttpProblemMapping.For<SourceBuildConfigurationAlreadyExists>(MapConfigurationAlreadyExists),
            HttpProblemMapping.For<SourceBuildConfigurationWebServiceNotFound>(MapWebServiceNotFound),
            HttpProblemMapping.For<SourceBuildConfigurationHasActiveJobs>(MapConfigurationHasActiveJobs),
            HttpProblemMapping.For<SourceBuildConfigurationRecipeCandidateChanged>(
                MapRecipeCandidateChanged),
            HttpProblemMapping.For<SourceBuildConfigurationUpdateConflict>(
                MapConfigurationUpdateConflict),
            HttpProblemMapping.For<SourceBuildLinkedRepositoryNotFound>(
                MapLinkedRepositoryNotFound),
            HttpProblemMapping.For<SourceBuildLinkedRepositoryCandidateChanged>(
                MapLinkedRepositoryCandidateChanged),
            HttpProblemMapping.For<SourceBuildJobNotFound>(MapJobNotFound)
        ]);

    private static HttpProblem MapConfigurationNotFound(SourceBuildConfigurationNotFound error) =>
        Problem(
            HttpStatusCode.NotFound,
            "source-builds.configurations.not-found",
            "Source build configuration not found",
            ("configurationId", error.ConfigurationId));

    private static HttpProblem MapConfigurationAlreadyExists(
        SourceBuildConfigurationAlreadyExists error) =>
        Problem(
            HttpStatusCode.Conflict,
            "source-builds.configurations.web-service-already-configured",
            "This web service already has a source build configuration",
            ("webServiceId", error.WebServiceId));

    private static HttpProblem MapWebServiceNotFound(
        SourceBuildConfigurationWebServiceNotFound error) =>
        Problem(
            HttpStatusCode.NotFound,
            "source-builds.configurations.web-service-not-found",
            "Web service not found",
            ("webServiceId", error.WebServiceId));

    private static HttpProblem MapConfigurationHasActiveJobs(
        SourceBuildConfigurationHasActiveJobs error) =>
        Problem(
            HttpStatusCode.Conflict,
            "source-builds.configurations.has-active-jobs",
            "Wait for active builds to finish before deleting this configuration",
            ("configurationId", error.ConfigurationId));

    private static HttpProblem MapRecipeCandidateChanged(
        SourceBuildConfigurationRecipeCandidateChanged error) =>
        Problem(
            HttpStatusCode.Conflict,
            "source-builds.configurations.recipe-candidate-changed",
            "The recipe candidate cannot be changed; relink the source repository candidate instead",
            ("configurationId", error.ConfigurationId));

    private static HttpProblem MapConfigurationUpdateConflict(
        SourceBuildConfigurationUpdateConflict error) =>
        Problem(
            HttpStatusCode.Conflict,
            "source-builds.configurations.update-conflict",
            "The source build configuration changed while it was being updated",
            ("configurationId", error.ConfigurationId));

    private static HttpProblem MapLinkedRepositoryNotFound(
        SourceBuildLinkedRepositoryNotFound error) =>
        Problem(
            HttpStatusCode.Conflict,
            "source-builds.configurations.linked-repository-not-found",
            "The linked source repository no longer exists",
            ("configurationId", error.ConfigurationId),
            ("sourceRepositoryId", error.SourceRepositoryId));

    private static HttpProblem MapLinkedRepositoryCandidateChanged(
        SourceBuildLinkedRepositoryCandidateChanged error) =>
        Problem(
            HttpStatusCode.Conflict,
            "source-builds.configurations.linked-repository-candidate-changed",
            "The linked source candidate changed; update or recreate the build configuration",
            ("configurationId", error.ConfigurationId),
            ("sourceRepositoryId", error.SourceRepositoryId));

    private static HttpProblem MapJobNotFound(SourceBuildJobNotFound error) =>
        Problem(
            HttpStatusCode.NotFound,
            "source-builds.jobs.not-found",
            "Source build job not found",
            ("buildJobId", error.BuildJobId));

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
