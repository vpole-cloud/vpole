import type { ConnectionEnvironmentReference, WebService } from './types'

export interface ReferenceRow {
  id: number
  name: string
  kind: 'connection' | 'secret'
  connectionId: string
  valueKind: ConnectionEnvironmentReference['valueKind']
  configurationKey: string
  secretId: string
  key: string
}
export const rowsFor = (service: WebService): ReferenceRow[] => [
  ...Object.entries(service.spec.config.connectionEnvironmentReferences ?? {}).map(
    ([name, reference], id) => ({
      id,
      name,
      kind: 'connection' as const,
      connectionId: reference.connectionId,
      valueKind: reference.valueKind,
      configurationKey: reference.configurationKey ?? '',
      secretId: '',
      key: '',
    }),
  ),
  ...Object.entries(service.spec.config.secretEnvironmentReferences ?? {}).map(
    ([name, reference], id) => ({
      id: id + Object.keys(service.spec.config.connectionEnvironmentReferences ?? {}).length,
      name,
      kind: 'secret' as const,
      secretId: reference.secretId,
      key: reference.key,
      connectionId: '',
      valueKind: 'Host' as const,
      configurationKey: '',
    }),
  ),
]
