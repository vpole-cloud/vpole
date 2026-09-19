// @vitest-environment jsdom
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { expect, it, vi } from 'vitest'
import { WebServiceForm } from './WebServiceForm'

it('selects an asynchronously loaded default target but preserves an explicit deselection', async () => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  const container = document.createElement('div')
  const root = createRoot(container)
  const onLoadComputeProfiles = vi.fn().mockResolvedValue([])
  const props = {
    mode: 'create' as const,
    resourceGroups: [],
    targetsStatus: 'success' as const,
    computeProfilesByTarget: {},
    submitStatus: 'idle' as const,
    onLoadTargets: vi.fn().mockResolvedValue([]),
    onLoadComputeProfiles,
    onSubmit: vi.fn(),
  }
  const target = { id: 'target', displayName: 'Target', workloadConfigurationRevision: 1, hasPublicIngress: true }
  try {
    await act(async () => { root.render(<WebServiceForm {...props} availableTargets={[]} />) })
    await act(async () => { root.render(<WebServiceForm {...props} availableTargets={[target]} />) })
    expect(onLoadComputeProfiles).toHaveBeenCalledWith('target')
    const options = [...container.querySelectorAll('button')].find((button) => button.textContent === 'Placement options')!
    await act(async () => { options.click() })
    const checkbox = container.querySelector<HTMLInputElement>('input[type="checkbox"]')!
    expect(checkbox.checked).toBe(true)
    await act(async () => { checkbox.click() })
    await act(async () => { root.render(<WebServiceForm {...props} availableTargets={[{ ...target }]} />) })
    expect(checkbox.checked).toBe(false)
  } finally {
    await act(async () => { root.unmount() })
  }
})
