import { renderToStaticMarkup } from 'react-dom/server'
import { describe, expect, it, vi } from 'vitest'
import { WebServiceForm } from './WebServiceForm'
import type { AvailableDeploymentTarget } from '../types'

const target = (id: string, displayName: string): AvailableDeploymentTarget => ({
  id,
  technicalName: id,
  displayName,
  workloadConfigurationRevision: 1,
  hasPublicIngress: true,
  publicBaseDomain: `${id}.example.test`,
})

const renderForm = (availableTargets: AvailableDeploymentTarget[]) =>
  renderToStaticMarkup(
    <WebServiceForm
      mode="create"
      resourceGroups={[]}
      availableTargets={availableTargets}
      targetsStatus="success"
      computeProfilesByTarget={{}}
      submitStatus="idle"
      onLoadTargets={vi.fn(async () => availableTargets)}
      onLoadComputeProfiles={vi.fn(async () => [])}
      onSubmit={vi.fn(async () => undefined)}
    />,
  )

describe('WebServiceForm deployment target visibility', () => {
  it('auto-selects the only eligible target without showing target checkboxes', () => {
    const markup = renderForm([target('target-a', 'Primary cluster')])

    expect(markup).toContain('Primary cluster is selected automatically.')
    expect(markup).toContain('Placement options')
    expect(markup).not.toContain('type="checkbox"')
  })

  it('shows explicit selectors when more than one target is eligible', () => {
    const markup = renderForm([
      target('target-a', 'Primary cluster'),
      target('target-b', 'Recovery cluster'),
    ])

    expect(markup.match(/type="checkbox"/g)).toHaveLength(2)
    expect(markup).toContain('Primary cluster')
    expect(markup).toContain('Recovery cluster')
    expect(markup).not.toContain('is selected automatically.')
  })
})
