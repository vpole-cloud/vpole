import { useContext } from 'react'
import { AppContext } from '../shell/AppContext'
import { gitOpsSourceResourceType, type GitOpsStatus } from '../data/gitOps'
import { Notice } from './components'
export function GitOwnershipNotice({ status }: { status?: GitOpsStatus | null }) {
  const app = useContext(AppContext)
  if (!status) return null
  const destination = { resourceTypeId: gitOpsSourceResourceType, resourceId: status.sourceId }
  const registered = app?.resourceTypes.some((type) => type.id === gitOpsSourceResourceType)
  return (
    <Notice>
      Git manages this configuration.{' '}
      {app && registered ? (
        <a
          href={app.navigation.resourceHref(destination)}
          onClick={(event) => {
            if (event.button === 0 && !event.ctrlKey && !event.metaKey && !event.shiftKey) {
              event.preventDefault()
              app.navigation.openResource(destination)
            }
          }}
        >
          Open managing source
        </a>
      ) : (
        <span>
          Source <code>{status.sourceId}</code>
        </span>
      )}
      .{status.blocker && <p>Configuration blocked: {status.blocker}.</p>}
      {status.pendingRemoval && <p>Declaration removed. Resource retained; removal is pending.</p>}
      {status.configurationApplied && (
        <p>Configuration applied. Runtime readiness is reported separately.</p>
      )}
    </Notice>
  )
}
