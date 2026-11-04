import type { GitOpsStatus } from '../data/gitOps'
import { Notice } from './components'

export function GitOwnershipNotice({ status }: { status?: GitOpsStatus | null }) {
  if (!status) return null
  return (
    <Notice>
      Git manages this configuration. <a href={status.sourceUrl}>Open managing source</a>.
      {status.blocker && <p>Configuration blocked: {status.blocker}.</p>}
      {status.pendingRemoval && <p>Declaration removed. Resource retained; removal is pending.</p>}
      {status.configurationApplied && (
        <p>Configuration applied. Runtime readiness is reported separately.</p>
      )}
    </Notice>
  )
}
