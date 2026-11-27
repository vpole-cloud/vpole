// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

using MongoDB.Driver;

namespace Pole.Features.SourceBuilds.Contracts;

/// <summary>Writes the active target in the caller transaction to fence admission against deletion.</summary>
public sealed record SourceBuildWebServiceAdmitted;

public union AdmitSourceBuildWebServiceOutcome(SourceBuildWebServiceAdmitted, SourceBuildWebServiceNotFound);

public delegate Task<AdmitSourceBuildWebServiceOutcome> AdmitSourceBuildWebService(
    AdmitSourceBuildWebServiceRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

/// <summary>Removes configuration for a deleted target, preserving all build history.</summary>
public delegate Task CleanupSourceBuildConfigurations(
    string tenantId,
    string webServiceId,
    CancellationToken cancellationToken = default);

public sealed record SourceBuildWebServiceHasActiveJobs(
    string WebServiceId
);

public sealed record NoActiveSourceBuilds;

public union EnsureNoWebServiceActiveSourceBuildsOutcome(NoActiveSourceBuilds, SourceBuildWebServiceHasActiveJobs);

public sealed record SourceBuildSourceRepositoryConfigurationExists(
    string SourceRepositoryId
);

public sealed record NoSourceRepositoryBuildConfiguration;

public union EnsureNoSourceRepositoryBuildConfigurationOutcome(
    NoSourceRepositoryBuildConfiguration,
    SourceBuildSourceRepositoryConfigurationExists);

/// <summary>
///     Resolves whether the WebServices target named by a source-build
///     configuration exists for its tenant. WebServices supplies this narrow
///     contribution without making SourceBuilds depend on WebServices.Contracts.
/// </summary>
public delegate Task<bool> SourceBuildWebServiceExists(
    string tenantId,
    string webServiceId,
    CancellationToken cancellationToken = default);

/// <summary>
///     Rejects deletion while nonterminal jobs exist, inside the transaction
///     which has already fenced new admissions by writing the WebService.
/// </summary>
public delegate Task<EnsureNoWebServiceActiveSourceBuildsOutcome> EnsureNoWebServiceActiveSourceBuilds(
    EnsureNoWebServiceActiveSourceBuildsRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

/// <summary>
///     Guards a source-repository deletion against source-build configuration that
///     still references it. The source-build feature owns this invariant.
/// </summary>
public delegate Task<EnsureNoSourceRepositoryBuildConfigurationOutcome> EnsureNoSourceRepositoryBuildConfiguration(
    SourceRepositoryConfigurationRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);

/// <summary>
///     Requests that the workload which owns a source-build target deploy the
///     produced image. The source-build feature owns this contribution boundary:
///     it records the job and needs the resulting release identifier to await
///     completion.
/// </summary>
public sealed record SourceBuildDeploymentImage(
    string RegistryHost,
    string ImageName,
    string Tag,
    string Digest,
    string PullSecretName
);

public sealed record SourceBuildDeploymentRequest(
    string TenantId,
    string WebServiceId,
    string BuildJobId,
    string SourceRepositoryId,
    string CommitSha,
    SourceBuildDeploymentAuthority Authority,
    string? ActorUserId,
    string? ActorEmail,
    SourceBuildDeploymentImage Image
);

public sealed record SourceBuildDeploymentResult(
    string ReleaseId,
    long Revision
);

public sealed record SourceBuildWebServiceNotFound(
    string WebServiceId
);

public sealed record SourceBuildWebServiceDeleting(
    string WebServiceId
);

public sealed record SourceBuildWebServiceHasNoActiveDeployments(
    string WebServiceId
);

public sealed record SourceBuildResourceDeploymentNotFound(
    string? ResourceDeploymentId
);

public sealed record SourceBuildResourceDeploymentChanged(
    string? ResourceDeploymentId
);

public sealed record SourceBuildResourceDeploymentCardinalityConflict;

public sealed record SourceBuildResourceDeploymentPendingDeletion(
    string? ResourceDeploymentId
);

public sealed record SourceBuildResourceDeploymentPlacementUnavailable(
    string? ResourceDeploymentId
);

public sealed record SourceBuildResourceDeploymentReplacementTargetMismatch(
    string? ResourceDeploymentId
);

public sealed record SourceBuildResourceDeploymentIngressConfigurationChanged(
    string ResourceDeploymentId
);

public sealed record SourceBuildResourceDeploymentRetryUnavailable(
    string ResourceDeploymentId
);

public sealed record SourceBuildResourceDeploymentComputeConfigurationChanged;

public union SourceBuildResourceDeploymentRejection(
    SourceBuildResourceDeploymentNotFound,
    SourceBuildResourceDeploymentChanged,
    SourceBuildResourceDeploymentCardinalityConflict,
    SourceBuildResourceDeploymentPendingDeletion,
    SourceBuildResourceDeploymentPlacementUnavailable,
    SourceBuildResourceDeploymentReplacementTargetMismatch,
    SourceBuildResourceDeploymentIngressConfigurationChanged,
    SourceBuildResourceDeploymentRetryUnavailable,
    SourceBuildResourceDeploymentComputeConfigurationChanged);

public sealed record SourceBuildWebServiceDeploymentRejected(
    string WebServiceId,
    SourceBuildResourceDeploymentRejection Reason
);

public enum SourceBuildWebServiceUpdateConflictReason
{
    ExpectedRevisionConflict,
    GitOwnedConfiguration,
    SupersededBuild,
}

public sealed record SourceBuildWebServiceUpdateConflict(
    string WebServiceId,
    SourceBuildWebServiceUpdateConflictReason Reason
        = SourceBuildWebServiceUpdateConflictReason.ExpectedRevisionConflict
);

public union SourceBuildWebServiceDeploymentError(
    SourceBuildWebServiceNotFound,
    SourceBuildWebServiceDeleting,
    SourceBuildWebServiceHasNoActiveDeployments,
    SourceBuildWebServiceDeploymentRejected,
    SourceBuildWebServiceUpdateConflict);

public union DeploySourceBuildWebServiceOutcome(SourceBuildDeploymentResult, SourceBuildWebServiceDeploymentError);

public delegate Task<DeploySourceBuildWebServiceOutcome> DeploySourceBuildWebService(
    SourceBuildDeploymentRequest request,
    CancellationToken cancellationToken = default);

/// <summary>
///     Reports the terminal state of a release created for a source build. The
///     adapter which owns the deployment-completed event maps that event into this
///     source-build contribution instead of making SourceBuilds consume a foreign
///     event contract.
/// </summary>
public sealed record SourceBuildDeploymentCompletion(
    string TenantId,
    string ReleaseId,
    string ReleaseOutcome,
    string? FailureMessage,
    DateTime CompletedAtUtc
);

public delegate Task CompleteSourceBuildJobOnDeployment(
    SourceBuildDeploymentCompletion completion,
    CancellationToken cancellationToken = default);

public enum SourceBuildAdmissionPurpose { Configure, Build }

public sealed record AdmitSourceBuildWebServiceRequest(
    string TenantId,
    string WebServiceId,
    string ConfigurationId,
    SourceBuildAdmissionPurpose Purpose
);

public sealed record EnsureNoWebServiceActiveSourceBuildsRequest(
    string TenantId,
    string WebServiceId
);

public sealed record SourceRepositoryConfigurationRequest(
    string TenantId,
    string SourceRepositoryId
);

public sealed record SourceBuildTargetRequest(
    string TenantId,
    string WebServiceId
);

public sealed record SourceBuildTargetSnapshot(
    string TenantId,
    string WebServiceId,
    long Revision,
    string? ConfigurationId
);

public union LoadSourceBuildTargetOutcome(SourceBuildTargetSnapshot, SourceBuildWebServiceNotFound);

public delegate Task<LoadSourceBuildTargetOutcome> LoadSourceBuildTarget(
    SourceBuildTargetRequest request,
    IClientSessionHandle session,
    CancellationToken cancellationToken);
