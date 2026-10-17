import { useApiScope } from '../../data/useApiScope'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useAppServices } from '../../shell/AppContext'

export function useWebServiceMutation(key: string, allowed: boolean) {
  const { api } = useAppServices()
  const scope = useApiScope(api)
  const mutation = useScopedMutation(`${scope.identity}:${key}:${allowed}`)
  const run = async <T>(
    action: (signal: AbortSignal) => Promise<T>,
    accepted?: (value: T, guard: () => void) => void | Promise<void>,
  ) => {
    if (!allowed || !scope.isCurrent()) return
    let guard = () => {}
    return mutation.run(
      async (signal) => {
        guard = () => {
          if (signal.aborted || !scope.isCurrent()) throw new DOMException('Aborted', 'AbortError')
        }
        guard()
        const value = await action(signal)
        guard()
        return value
      },
      async (value) => {
        guard()
        await accepted?.(value, guard)
        guard()
      },
    )
  }
  return { ...mutation, run }
}
