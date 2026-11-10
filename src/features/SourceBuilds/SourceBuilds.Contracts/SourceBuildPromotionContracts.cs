using MongoDB.Driver;

namespace Pole.Features.SourceBuilds.Contracts;

public sealed record SourceBuildDeploymentAuthority(
    string ConfigurationId,
    long ConfigurationRevision,
    long Generation,
    string WorkerId,
    long Attempt
);

public sealed record SourceBuildPromotionInput(
    string SourceRepositoryId,
    string CommitSha,
    SourceBuildPromotionArtifact Artifact
);

public sealed record SourceBuildPromotionArtifact(
    string Reference,
    string Digest
);

public sealed record SourceBuildPromotionRequest(
    string TenantId,
    string BuildJobId,
    string WebServiceId,
    SourceBuildDeploymentAuthority Authority,
    SourceBuildPromotionInput Input
);

public sealed record SourceBuildPromotionAuthorized;

public sealed record SourceBuildAlreadyPromoted(
    string ReleaseId,
    long Revision
);

public sealed record SourceBuildPromotionSuperseded;

public union BeginSourceBuildPromotionOutcome(
    SourceBuildPromotionAuthorized,
    SourceBuildAlreadyPromoted,
    SourceBuildPromotionSuperseded);

public delegate Task<BeginSourceBuildPromotionOutcome> BeginSourceBuildPromotion(
    SourceBuildPromotionRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);

public sealed record CompleteSourceBuildPromotionRequest(
    SourceBuildPromotionRequest Promotion,
    string ReleaseId,
    long Revision
);

/// <summary>Records promotion together with the owner's workload, placement, release and outbox writes.</summary>
public delegate Task CompleteSourceBuildPromotion(
    CompleteSourceBuildPromotionRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
