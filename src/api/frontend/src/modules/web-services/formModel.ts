import type { WebService, WebServiceDeploymentInput } from '../../types'

export const parseEnvironmentVariables = (value: string) =>
  value
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => {
      const [key, ...rest] = line.split('=')
      return { key: key?.trim() ?? '', value: rest.join('=').trim() }
    })
    .filter((item) => item.key)

export type SelectedDeploymentInput = WebServiceDeploymentInput & { deploymentTargetId: string }

export const deploymentConfiguration = (
  targetId: string,
  current?: WebServiceDeploymentInput,
): SelectedDeploymentInput => ({
  deploymentTargetId: targetId,
  fixedReplicas: current?.fixedReplicas ?? 1,
  computeProfileId: current?.computeProfileId,
})

export const fieldsFor = (service: WebService) => ({
  name: service.name,
  resourceGroupId: service.resourceGroupId,
  isolationScopeId: service.isolationScopeId ?? '',
  exposure: service.exposure.mode,
  imageServer: service.spec.image.server,
  imageName: service.spec.image.name,
  imageTag: service.spec.image.tag,
  probePath: service.spec.probes?.readiness.path ?? '/health',
  probePort: service.spec.probes?.readiness.port ?? '8080',
  servicePort: String(service.spec.config.portMappings[0]?.servicePort ?? 80),
  targetPort: service.spec.config.portMappings[0]?.targetPort ?? '8080',
  portName: service.spec.config.portMappings[0]?.name ?? 'http',
  environmentText: Object.entries(service.spec.config.environmentVariables)
    .map(([key, value]) => `${key}=${value}`)
    .join('\n'),
})
