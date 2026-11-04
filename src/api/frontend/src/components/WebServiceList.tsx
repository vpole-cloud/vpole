import { imageDescription } from '../modules/web-services/imageModel'
import type { LoadStatus, ResourceGroup, WebService } from '../types'

interface WebServiceListProps {
  items: WebService[]
  groups: ResourceGroup[]
  status: LoadStatus
  error?: string
}

const resolveResourceGroupName = (webService: WebService, groups: ResourceGroup[]) =>
  groups.find((group) => group.id === webService.resourceGroupId)?.name

export function WebServiceList({ items, groups, status, error }: WebServiceListProps) {
  return (
    <section className="panel">
      <div className="panel__header">
        <div>
          <p className="panel__eyebrow">Runtime</p>
          <h2 className="panel__title">Web services</h2>
        </div>
        <span className="panel__meta">{items.length} visible</span>
      </div>

      {status === 'loading' && <div className="empty-state">Loading deployed services...</div>}

      {status === 'error' && (
        <div className="empty-state empty-state--error">
          {error || 'Unable to load web services.'}
        </div>
      )}

      {status === 'success' && items.length === 0 && (
        <div className="empty-state">
          No web services yet. The shell is ready for deployments once the API returns them.
        </div>
      )}

      {status === 'success' && items.length > 0 && (
        <div className="list">
          {items.map((item) => {
            const image = imageDescription(item.spec)
            const groupName = resolveResourceGroupName(item, groups)

            return (
              <article className="list-row" key={item.id}>
                <div className="list-row__primary">
                  <h3>{item.name}</h3>
                  <p>
                    {item.deployments
                      .flatMap((deployment) => deployment.ingress?.hostnames ?? [])
                      .join(', ') || 'No public hostname reported yet.'}
                  </p>
                </div>
                <div className="list-row__secondary">
                  <span className="status-pill status-pill--accent">
                    {item.deployments.length} target{item.deployments.length === 1 ? '' : 's'}
                  </span>
                  <span className="list-row__meta">
                    {groupName || 'Unknown group'} · {image}
                  </span>
                </div>
              </article>
            )
          })}
        </div>
      )}
    </section>
  )
}
