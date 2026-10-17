import type { WebService, ResourceGroup } from '../types'

const defaultService: WebService = {
  id: 'service',
  name: 'sample',
  resourceGroupId: 'group',
  activeRevision: 1,
  exposure: { mode: 'Private', generatedHostnameLabelPrefix: '' },
  spec: {
    image: { server: 'docker.io', name: 'library/nginx', tag: 'stable' },
    config: {
      environmentVariables: {},
      portMappings: [{ name: 'http', servicePort: 80, targetPort: '80', protocol: 'TCP' }],
    },
    probes: {
      startup: { path: '/', port: '80' },
      liveness: { path: '/', port: '80' },
      readiness: { path: '/', port: '80' },
    },
  },
  deployments: [
    {
      deploymentId: 'deployment',
      deploymentTargetId: 'target',
      runtimeCellId: 'cell',
      desiredGeneration: 1,
      observedGeneration: 0,
      status: 'PendingApply',
      resourceRevision: 1,
      fixedReplicas: 1,
      deploymentRevision: 1,
      desiredOperation: 'Apply',
      endpoints: [],
      compute: {
        displayName: 'Small',
        profile: { computeProfileId: 'small', computeProfileRevision: 1 },
        requests: { cpuMillis: 100, memoryMi: 128 },
        limits: { cpuMillis: 200, memoryMi: 256 },
        extendedResources: [],
      },
    },
  ],
}

export function webService(overrides: Partial<WebService> = {}): WebService {
  return { ...structuredClone(defaultService), ...overrides }
}

export function resourceGroup(overrides: Partial<ResourceGroup> = {}): ResourceGroup {
  return {
    id: 'group',
    name: 'Applications',
    description: '',
    tags: [],
    resourceCount: 1,
    ...overrides,
  }
}
