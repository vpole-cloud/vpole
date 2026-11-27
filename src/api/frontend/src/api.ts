// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

export {
  fetchResourceDeploymentSet,
  fetchResourceEndpoints,
  retryResourceDeployment,
  fetchResourceOperations,
  fetchResourceObservation,
  fetchResourceDiagnostics,
} from './modules/operations/api'
export type { ResourceOperationsFilter } from './modules/operations/api'
export {
  fetchStorageObservation,
  fetchStorageProfiles,
  fetchPlatformDeploymentTargets,
  fetchKubernetesStorageClasses,
  fetchKubernetesPersistentVolumes,
  fetchKubernetesPersistentVolumeClaims,
  createStorageProfile,
  updateStorageProfile,
} from './modules/platform/storageApi'
export {
  fetchGitProviderConnections,
  beginGitHubInstallation,
  disconnectGitProviderConnection,
  fetchProviderRepositories,
  fetchProviderBranches,
  analyzeProviderRepository,
  linkProviderRepository,
  fetchSourceRepositories,
  reanalyzeSourceRepository,
  unlinkSourceRepository,
} from './modules/platform/gitApi'
export { fetchProtectedGreeting, searchResources } from './modules/platform/discoveryApi'
export {
  SecretApiError,
  fetchSecrets,
  fetchSecret,
  createSecret,
  rotateSecret,
  deleteSecret,
  fetchSecretUsage,
  fetchSecretProjections,
  bindSecret,
  detachSecret,
} from './modules/secrets/api'
export {
  fetchDomainZones,
  DomainApiError,
  fetchDomainZone,
  fetchDomainZoneBindings,
  createDomainZone,
  deleteDomainZone,
  fetchWebServiceHostnameBindings,
  addWebServiceHostnameBinding,
  deleteWebServiceHostnameBinding,
} from './modules/domains/api'
export {
  fetchIsolationScopes,
  fetchResourceGroups,
  fetchResourceGroup,
  fetchResourceGroupResources,
  createResourceGroup,
  updateResourceGroup,
  deleteResourceGroup,
} from './modules/resource-groups/api'
export {
  fetchRedisServices,
  fetchRedisService,
  createRedisService,
  updateRedisService,
  deleteRedisService,
} from './modules/redis/api'
export {
  fetchPostgresClusters,
  fetchPostgresCluster,
  fetchPostgresCapabilities,
  createPostgresCluster,
  updatePostgresCluster,
  deletePostgresCluster,
  fetchPostgresConnectionString,
  rotatePostgresPassword,
} from './modules/postgres/api'
export { ApiError, validationMessage } from './api/transport'
export { fetchTenants, normalizeTenantRole } from './api/tenants'
export {
  updateWebServiceDependencies,
  fetchWebServices,
  fetchWebService,
  createWebService,
  updateWebService,
  fetchWebServiceDeployments,
  addWebServiceDeployment,
  updateWebServiceDeployment,
  deleteWebServiceDeployment,
  retryWebServiceDeployment,
  fetchWebServiceReleases,
  redeployWebService,
  rollbackWebService,
  deleteWebService,
} from './modules/web-services/api'
export {
  fetchSourceBuildJob,
  fetchSourceBuildLogs,
  fetchSourceBuildConfigurations,
  createSourceBuildConfiguration,
  deleteSourceBuildConfiguration,
  updateSourceBuildRecipe,
  startSourceBuild,
  fetchSourceBuildJobs,
} from './modules/web-services/buildApi'
export {
  fetchAvailableStorageProfiles,
  fetchAvailableDeploymentTargets,
  fetchAvailableComputeProfiles,
} from './api/eligibility'
export {
  fetchResourceConnections,
  createResourceConnection,
  deleteResourceConnection,
} from './modules/web-services/connectionApi'
export { RuntimeLogUnavailableError, readResourceRuntimeLogs } from './api/runtimeLogApi'

export {
  fetchTenantMembers,
  fetchTenantInvitations,
  inviteTenantMember,
  changeTenantInvitation,
  updateTenantMember,
  removeTenantMember,
  previewTenantInvitation,
  acceptTenantInvitation,
} from './modules/tenants/api'
