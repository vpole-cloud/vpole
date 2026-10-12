import type { RealtimeChange, RealtimeSubscription } from '../../realtime/types'
import type { ProviderBranch, RepositoryAnalysis, SourceRepository } from '../../types'

const metadata = new Set(['connections', 'sources'])
const discovery = new Set(['repositories', 'branches'])
export function gitInterests(tenantId: string | undefined, key: readonly unknown[]): RealtimeSubscription[] {
  return key[0] === 'git' && metadata.has(String(key[1])) ? [{ topic: 'git', tenantId }] : []
}
export function matchesGitQuery(key: readonly unknown[], change: RealtimeChange) {
  // Connection IDs and linked-source IDs share the topic; the authoritative HTTP
  // reads resolve their relationships. External Git changes still need the TTL.
  return change.topic === 'git' && key[0] === 'git' && (metadata.has(String(key[1])) || discovery.has(String(key[1])))
}

export function staleGitAnalysis(analysis: RepositoryAnalysis | null, branches: readonly ProviderBranch[], complete: boolean, advanced: boolean) {
  if (!analysis || !advanced) return false
  const branch = branches.find(item => item.name === analysis.branch)
  return branch ? branch.commitSha !== analysis.commitSha : complete
}
export function sourceSelectionCurrent(selected: SourceRepository, current: readonly SourceRepository[]) {
  const source = current.find(item => item.id === selected.id)
  return Boolean(source && source.connectionId === selected.connectionId && source.externalRepositoryId === selected.externalRepositoryId &&
    source.branch === selected.branch && source.commitSha === selected.commitSha && JSON.stringify(source.selectedPlan) === JSON.stringify(selected.selectedPlan))
}
