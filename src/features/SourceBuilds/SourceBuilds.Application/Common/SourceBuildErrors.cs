namespace Pole.Features.SourceBuilds.Application.Common;

public sealed record SourceBuildConfigurationNotFound(string ConfigurationId);

public sealed record SourceBuildConfigurationAlreadyExists(string WebServiceId);

public sealed record SourceBuildConfigurationWebServiceNotFound(string WebServiceId);

public sealed record SourceBuildConfigurationInUse(
    string ConfigurationId,
    string WebServiceId);

public sealed record SourceBuildConfigurationHasActiveJobs(string ConfigurationId);

public sealed record SourceBuildConfigurationNameConflict(string TechnicalName);

public sealed record SourceBuildConfigurationRepositoryChanged(string SourceRepositoryId);

public sealed record SourceBuildConfigurationGitOwned(string ConfigurationId);

public sealed record SourceBuildConfigurationUpdateConflict(string ConfigurationId);

public sealed record SourceBuildLinkedRepositoryNotFound(
    string ConfigurationId,
    string SourceRepositoryId);

public sealed record SourceBuildJobNotFound(string BuildJobId);
