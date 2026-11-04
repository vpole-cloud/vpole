import { useApiScope } from '../../data/useApiScope'
import { useScopedQuery } from '../../data/useScopedQuery'
import type { ScopedQueryData } from '../../data/querySession'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useAppServices } from '../../shell/AppContext'
import { usePageVisible } from '../../realtime/usePageVisible'
import { useProviderInvalidation } from '../../data/useProviderInvalidation'
import type { GitOpsApi } from './api'
export function useGitOpsQuery<T>(
  parts: readonly unknown[],
  active: boolean,
  interval: number | false | ((value: T | null) => number | false),
  load: (api: GitOpsApi, signal: AbortSignal) => Promise<T>,
) {
  const { api, tenant } = useAppServices()
  const origin = useApiScope(api)
  const visible = usePageVisible()
  const cached =
    origin.session.client.getQueryData<ScopedQueryData<T>>(origin.key(['gitops', ...parts]))
      ?.value ?? null
  const polling = typeof interval === 'function' ? interval(cached) : interval
  const query = useScopedQuery<T>({
    scope: [api.baseUrl, api.tenantId],
    key: ['gitops', ...parts],
    enabled: Boolean(tenant.active) && active && visible,
    interval: polling,
    keepPrevious: false,
    load: (signal) =>
      load({ baseUrl: api.baseUrl, tenantId: api.tenantId, credential: origin.credential }, signal),
  })
  return { ...query, origin }
}
export function useGitOpsMutation(id: string, allowed: boolean) {
  const { api } = useAppServices()
  const origin = useProviderInvalidation(api)
  const mutation = useScopedMutation(`${origin.identity}:gitops:${id}:${allowed}`)
  const context: GitOpsApi = {
    baseUrl: api.baseUrl,
    tenantId: api.tenantId,
    credential: origin.credential,
  }
  async function run<T>(
    action: (api: GitOpsApi, signal: AbortSignal) => Promise<T>,
    accepted: (value: T, guard: () => void) => void | Promise<void>,
  ) {
    if (!allowed || !origin.isCurrent()) return
    const result = await mutation.run(
      async (signal) => {
        const value = await action(context, signal)
        if (signal.aborted || !origin.isCurrent()) throw new DOMException('Aborted', 'AbortError')
        return { value, signal }
      },
      async ({ value, signal }) => {
        const guard = () => {
          if (signal.aborted || !origin.isCurrent()) throw new DOMException('Aborted', 'AbortError')
        }
        guard()
        await origin.invalidate([['gitops'], ['resource-groups'], ['overview'], ['search']])
        guard()
        await accepted(value, guard)
        guard()
      },
    )
    return result?.value
  }
  return { ...mutation, run, context, origin }
}
