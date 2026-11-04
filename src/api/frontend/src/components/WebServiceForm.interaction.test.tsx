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
  const target = {
    id: 'target',
    technicalName: 'target',
    displayName: 'Target',
    workloadConfigurationRevision: 1,
    hasPublicIngress: true,
  }
  try {
    await act(async () => {
      root.render(<WebServiceForm {...props} availableTargets={[]} />)
    })
    await act(async () => {
      root.render(<WebServiceForm {...props} availableTargets={[target]} />)
    })
    expect(onLoadComputeProfiles).toHaveBeenCalledWith('target')
    const options = [...container.querySelectorAll('button')].find(
      (button) => button.textContent === 'Placement options',
    )!
    await act(async () => {
      options.click()
    })
    const checkbox = container.querySelector<HTMLInputElement>('input[type="checkbox"]')!
    expect(checkbox.checked).toBe(true)
    await act(async () => {
      checkbox.click()
    })
    await act(async () => {
      root.render(<WebServiceForm {...props} availableTargets={[{ ...target }]} />)
    })
    expect(checkbox.checked).toBe(false)
  } finally {
    await act(async () => {
      root.unmount()
    })
  }
})

it('replaces private target selections with the refreshed public placement before submitting', async () => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  const container = document.createElement('div')
  const root = createRoot(container)
  const onSubmit = vi.fn().mockResolvedValue(undefined)
  const props = {
    mode: 'create' as const,
    resourceGroups: [{ id: 'group', name: 'Group', description: '', resourceCount: 0, tags: [] }],
    targetsStatus: 'success' as const,
    computeProfilesByTarget: {},
    submitStatus: 'idle' as const,
    onLoadTargets: vi.fn().mockResolvedValue([]),
    onLoadComputeProfiles: vi.fn().mockResolvedValue([]),
    onSubmit,
  }
  const privateTarget = {
    id: 'private',
    technicalName: 'private',
    displayName: 'Private cluster',
    workloadConfigurationRevision: 1,
    hasPublicIngress: false,
  }
  const publicTarget = {
    id: 'public',
    technicalName: 'public',
    displayName: 'Public cluster',
    workloadConfigurationRevision: 1,
    hasPublicIngress: true,
  }
  try {
    await act(async () => {
      root.render(<WebServiceForm {...props} availableTargets={[privateTarget, publicTarget]} />)
    })
    for (const [placeholder, value] of [
      ['pole-api', 'service'],
      ['docker.io', 'docker.io'],
      ['library/nginx', 'library/nginx'],
    ]) {
      const input = container.querySelector<HTMLInputElement>(
        `input[placeholder="${placeholder}"]`,
      )!
      await act(async () => {
        Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!.call(
          input,
          value,
        )
        input.dispatchEvent(new Event('input', { bubbles: true }))
      })
    }
    await act(async () => {
      container.querySelector<HTMLInputElement>('input[type="checkbox"]')!.click()
    })
    const exposure = [...container.querySelectorAll('select')].find(
      (select) => select.value === 'Private',
    )!
    await act(async () => {
      exposure.value = 'Public'
      exposure.dispatchEvent(new Event('change', { bubbles: true }))
    })
    await act(async () => {
      root.render(
        <WebServiceForm
          {...props}
          availableTargets={[privateTarget, publicTarget]}
          targetsStatus="loading"
        />,
      )
    })
    const disabledWhileLoading =
      container.querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled
    await act(async () => {
      root.render(<WebServiceForm {...props} availableTargets={[publicTarget]} />)
    })
    await act(async () => {
      container
        .querySelector('form')!
        .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }))
    })
    expect(onSubmit).toHaveBeenCalledOnce()
    expect(onSubmit.mock.calls[0][0].targets).toEqual([
      { deploymentTargetId: 'public', fixedReplicas: 1 },
    ])
    expect(disabledWhileLoading).toBe(true)
  } finally {
    await act(async () => {
      root.unmount()
    })
  }
})

it('blocks creation without eligible compute profiles and recovers once an eligible default is available', async () => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  const container = document.createElement('div')
  const root = createRoot(container)
  const props = {
    mode: 'create' as const,
    resourceGroups: [{ id: 'group', name: 'Group', description: '', tags: [], resourceCount: 0 }],
    availableTargets: [
      {
        id: 'target',
        technicalName: 'target',
        displayName: 'Cluster',
        workloadConfigurationRevision: 1,
        hasPublicIngress: true,
      },
    ],
    targetsStatus: 'success' as const,
    computeProfilesStatusByTarget: { target: 'success' as const },
    submitStatus: 'idle' as const,
    onLoadTargets: vi.fn().mockResolvedValue([]),
    onLoadComputeProfiles: vi.fn().mockResolvedValue([]),
    onSubmit: vi.fn(),
  }
  try {
    await act(async () =>
      root.render(<WebServiceForm {...props} computeProfilesByTarget={{ target: [] }} />),
    )
    expect(container.textContent).toContain('No eligible compute profile is available.')
    expect(container.querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled).toBe(true)
    await act(async () =>
      root.render(
        <WebServiceForm
          {...props}
          computeProfilesByTarget={{
            target: [
              {
                id: 'default',
                technicalName: 'default',
                displayName: 'Default',
                description: '',
                isDefault: true,
                architecture: 'amd64',
                extendedResources: [],
                revision: 1,
                resources: {
                  requests: { cpuMillis: 100, memoryMi: 128 },
                  limits: { cpuMillis: 200, memoryMi: 256 },
                },
              },
            ],
          }}
        />,
      ),
    )
    expect(container.textContent).not.toContain('No eligible compute profile is available.')
    expect(container.querySelector<HTMLButtonElement>('button[type="submit"]')!.disabled).toBe(
      false,
    )
  } finally {
    await act(async () => root.unmount())
  }
})
