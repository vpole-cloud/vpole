using System.Net;
using Pole.ASP.Helpers;

namespace Pole.Features.SourceBuilds.Application.Common;

public static class SourceBuildHttpMappings
{
    public static HttpProblem MapConfigurationNotFound(SourceBuildConfigurationNotFound error) => Problem(
        HttpStatusCode.NotFound,
        "source-builds.configurations.not-found",
        "Source build configuration not found",
        ("configurationId", error.ConfigurationId));

    public static HttpProblem MapConfigurationAlreadyExists(SourceBuildConfigurationAlreadyExists error) => Problem(
        HttpStatusCode.Conflict,
        "source-builds.configurations.web-service-already-configured",
        "This web service already has a source build configuration",
        ("webServiceId", error.WebServiceId));

    public static HttpProblem MapWebServiceNotFound(SourceBuildConfigurationWebServiceNotFound error) => Problem(
        HttpStatusCode.NotFound,
        "source-builds.configurations.web-service-not-found",
        "Web service not found",
        ("webServiceId", error.WebServiceId));

    public static HttpProblem MapConfigurationInUse(SourceBuildConfigurationInUse error) => Problem(
        HttpStatusCode.Conflict,
        "source-builds.configurations.in-use",
        "This configuration is still bound to its web service",
        ("configurationId", error.ConfigurationId),
        ("webServiceId", error.WebServiceId));

    public static HttpProblem MapConfigurationHasActiveJobs(SourceBuildConfigurationHasActiveJobs error) => Problem(
        HttpStatusCode.Conflict,
        "source-builds.configurations.has-active-jobs",
        "Wait for active builds to finish before deleting this configuration",
        ("configurationId", error.ConfigurationId));

    public static HttpProblem MapConfigurationNameConflict(SourceBuildConfigurationNameConflict error) => Problem(
        HttpStatusCode.Conflict,
        "source-builds.configurations.name-conflict",
        "A source build configuration already uses this technical name",
        ("technicalName", error.TechnicalName));

    public static HttpProblem MapConfigurationRepositoryChanged(SourceBuildConfigurationRepositoryChanged error) =>
        Problem(
            HttpStatusCode.Conflict,
            "source-builds.configurations.repository-changed",
            "The source repository changed",
            ("sourceRepositoryId", error.SourceRepositoryId));

    public static HttpProblem MapConfigurationGitOwned(SourceBuildConfigurationGitOwned error) => Problem(
        HttpStatusCode.Conflict,
        "source-builds.configurations.git-owned",
        "Change this configuration through its managing Git source",
        ("configurationId", error.ConfigurationId));

    public static HttpProblem MapConfigurationUpdateConflict(SourceBuildConfigurationUpdateConflict error) => Problem(
        HttpStatusCode.Conflict,
        "source-builds.configurations.update-conflict",
        "The source build configuration changed while it was being updated",
        ("configurationId", error.ConfigurationId));

    public static HttpProblem MapLinkedRepositoryNotFound(SourceBuildLinkedRepositoryNotFound error) => Problem(
        HttpStatusCode.Conflict,
        "source-builds.configurations.linked-repository-not-found",
        "The linked source repository no longer exists",
        ("configurationId", error.ConfigurationId),
        ("sourceRepositoryId", error.SourceRepositoryId));

    public static HttpProblem MapJobNotFound(SourceBuildJobNotFound error) => Problem(
        HttpStatusCode.NotFound,
        "source-builds.jobs.not-found",
        "Source build job not found",
        ("buildJobId", error.BuildJobId));

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
