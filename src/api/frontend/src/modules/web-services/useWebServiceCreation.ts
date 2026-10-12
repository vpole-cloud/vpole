import { useReducer } from 'react'
import { createSourceBuildConfiguration, createWebService, fetchSourceBuildConfigurations,
  fetchSourceBuildJobs, fetchWebServices, startSourceBuild } from '../../api'
import { invalidateWebServiceState } from './invalidate'
import { ApiError } from '../../api/transport'
import { useApiScope } from '../../data/useApiScope'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useAppServices } from '../../shell/AppContext'
import type { WebServiceInput } from '../../types'
import { creationCommand, creationReducer, type AcceptedCreation, type CreationCommand, type CreationPhase } from './creationWorkflow'

export function useWebServiceCreation(onCompleted: (id: string, mode: 'image' | 'git') => void) {
  const { api, tenant } = useAppServices()
  const scope = useApiScope(api)
  const mutation = useScopedMutation(`${scope.identity}:${tenant.canManage}`)
  const [state, dispatch] = useReducer(creationReducer, { kind: 'editing' })
  const busy = mutation.busy
  const error = state.kind === 'editing' || state.kind === 'recoverable-failure' ? state.error : null
  const accepted = 'accepted' in state ? state.accepted : {}

  const execute = async (command: CreationCommand, prior: AcceptedCreation, recover: boolean) => {
    let phase: CreationPhase = prior.serviceId ? 'configuring' : 'creating'
    const accepted: AcceptedCreation = { ...prior }
    return mutation.run(async (signal) => {
      const guard = () => {
        if (signal.aborted || !scope.isCurrent()) throw new DOMException('Aborted', 'AbortError')
      }
      try {
        guard()
        if (!accepted.serviceId) {
          dispatch({ type: 'begin', command })
          if (recover) {
            // The create endpoint has no idempotency key. Never repeat an uncertain create.
            const page = await fetchWebServices(api.baseUrl, scope.credential(), api.tenantId, signal, { pageNumber: 1, pageSize: 100, search: command.input.name })
            guard()
            const candidate = page.items.find((item) => item.name === command.input.name && item.resourceGroupId === command.input.resourceGroupId)
            throw new Error(candidate ? 'A matching service exists. Inspect All services to confirm its configuration before continuing source setup; this creation request will not be repeated.' :
              'The creation result is still unknown. Inspect All services before creating another service; this request will not be repeated.')
          } else {
            const created = await createWebService(api.baseUrl, command.input, scope.credential(), api.tenantId)
            guard()
            accepted.serviceId = created.webServiceId
          }
        }
        guard()
        const serviceId = accepted.serviceId
        if (command.mode === 'git') {
          phase = 'configuring'
          dispatch({ type: 'created', command, serviceId })
          // Read before every resumed write: a response can be lost after server acceptance.
          const configurations = await fetchSourceBuildConfigurations(api.baseUrl, scope.credential(), api.tenantId, signal)
          guard()
          const existing = configurations.find((item) => item.webServiceId === serviceId)
          if (existing && existing.sourceRepositoryId !== command.sourceId) throw new Error('This service already has a different source. Open Builds to inspect its configuration.')
          accepted.configurationId = existing?.id ?? accepted.configurationId
          if (!accepted.configurationId) {
            const configuration = await createSourceBuildConfiguration(api.baseUrl, command.sourceId, serviceId, scope.credential(), api.tenantId)
            guard()
            accepted.configurationId = configuration.id
          }
          guard()
          phase = 'queueing'
          const configurationId = accepted.configurationId
          dispatch({ type: 'configured', command, serviceId, configurationId })
          const jobs = await fetchSourceBuildJobs(api.baseUrl, serviceId, scope.credential(), api.tenantId, signal)
          guard()
          accepted.jobId = jobs.items.find((job) => job.configurationId === configurationId)?.id ?? accepted.jobId
          if (!accepted.jobId) {
            const job = await startSourceBuild(api.baseUrl, configurationId, scope.credential(), api.tenantId)
            guard()
            accepted.jobId = job.id
          }
        }
        await invalidateWebServiceState(scope, [['git', 'sources']]); guard()
        dispatch({ type: 'completed', command, accepted: { ...accepted, serviceId } })
        onCompleted(serviceId, command.mode)
      } catch (error) {
        guard()
        dispatch({ type: 'failed', command, phase, accepted, error,
          rejected: error instanceof ApiError && error.status >= 400 && error.status < 500 })
        throw error
      }
    })
  }
  return { state, busy, error, accepted, locked: state.kind !== 'editing',
    submit: (input: WebServiceInput, mode: 'image' | 'git', sourceId?: string) => execute(creationCommand(input, mode, sourceId), {}, false),
    retry: () => state.kind === 'recoverable-failure' ? execute(state.command, state.accepted, true) : Promise.resolve(),
  }
}
