export interface SecretMetadata {
  id: string
  tenantId: string
  resourceGroupId: string
  isolationScopeId: string
  name: string
  purpose: string
  keyNames: string[]
  currentVersion: number
  revision: number
  lifecycle: string
  createdAtUtc: string
  updatedAtUtc: string
  managedBy?: { resourceType: string; resourceId: string; bindingId: string } | null
}

export interface SecretInput {
  resourceGroupId: string
  isolationScopeId: string
  name: string
  purpose: string
  values: Record<string, string>
}

export interface SecretBindingInput {
  consumerType: string
  consumerId: string
  allDeployments: boolean
  deploymentIds: string[]
  keyMappings: Record<string, string>
}

export interface SecretUsage {
  binding: SecretBindingInput & { id: string }
  affectedDeploymentIds: string[]
}

export interface SecretProjection {
  resourceDeploymentId: string
  bindingId: string
  deploymentTargetId: string
  desiredVersion: number
  appliedVersion: number | null
  health: 'Pending' | 'Ready' | 'Failed' | 'Deleted'
  lastAttemptAtUtc: string | null
  failureCode: string | null
}
