namespace Pole.Features.SourceBuilds.Application.Common;

public sealed record SourceBuildConfigurationNotFound(string ConfigurationId);

public sealed record SourceBuildConfigurationAlreadyExists(string WebServiceId);

public sealed record SourceBuildConfigurationWebServiceNotFound(string WebServiceId);

public sealed record SourceBuildConfigurationHasActiveJobs(string ConfigurationId);

public sealed record SourceBuildConfigurationRecipeCandidateChanged(string ConfigurationId);

public sealed record SourceBuildConfigurationUpdateConflict(string ConfigurationId);

public sealed record SourceBuildLinkedRepositoryNotFound(
    string ConfigurationId,
    string SourceRepositoryId);

public sealed record SourceBuildLinkedRepositoryCandidateChanged(
    string ConfigurationId,
    string SourceRepositoryId);

public sealed record SourceBuildJobNotFound(string BuildJobId);
