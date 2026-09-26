using MongoDB.Driver;
using Pole.Workloads;

namespace Pole.Features.ResourceDeployments.Contracts;

public sealed record ResourceDeploymentReferenceUnavailable;
public sealed record ResourceDeploymentReferencesAdmitted;
public union AdmitResourceDeploymentReferencesOutcome(
    ResourceDeploymentReferencesAdmitted,
    ResourceDeploymentReferenceUnavailable);

public delegate Task<AdmitResourceDeploymentReferencesOutcome> AdmitResourceDeploymentReferences(
    string tenantId,
    string resourceGroupId,
    string isolationScopeId,
    ResourceTypeId consumerType,
    string consumerId,
    bool allDeployments,
    IReadOnlyCollection<string> deploymentIds,
    IClientSessionHandle session,
    CancellationToken cancellationToken = default);
