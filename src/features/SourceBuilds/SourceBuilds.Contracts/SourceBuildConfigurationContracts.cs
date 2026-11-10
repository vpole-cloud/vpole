using MongoDB.Driver;

namespace Pole.Features.SourceBuilds.Contracts;

public sealed record SourceBuildConfigurationSnapshot(
    string Id,
    string TenantId,
    string TechnicalName,
    string SourceRepositoryId,
    string WebServiceId,
    string Dockerfile,
    long Revision,
    long BuildGeneration
);

public sealed record SourceBuildConfigurationReferenceRequest(
    string TenantId,
    string ConfigurationId
);

public sealed record NamedSourceBuildConfigurationRequest(
    string TenantId,
    string TechnicalName
);

public sealed record SourceBuildConfigurationReferenceMissing;

public union FindSourceBuildConfigurationOutcome(
    SourceBuildConfigurationSnapshot,
    SourceBuildConfigurationReferenceMissing);

public delegate Task<FindSourceBuildConfigurationOutcome> LoadSourceBuildConfigurationReference(
    SourceBuildConfigurationReferenceRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

public delegate Task<FindSourceBuildConfigurationOutcome> FindNamedSourceBuildConfiguration(
    NamedSourceBuildConfigurationRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

public sealed record SourceBuildConfigurationFenced;

public sealed record SourceBuildConfigurationReferenceConflict;

public union FenceSourceBuildConfigurationOutcome(
    SourceBuildConfigurationFenced,
    SourceBuildConfigurationReferenceConflict);

public delegate Task<FenceSourceBuildConfigurationOutcome> FenceSourceBuildConfiguration(
    SourceBuildConfigurationSnapshot expected,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
