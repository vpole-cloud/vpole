// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

// @vitest-environment jsdom
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { expect, it, vi } from 'vitest'
import { Tabs } from './Tabs'

it('keeps the active section visible when the viewport narrows and removes resize work on close', async () => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  const container = document.createElement('div')
  document.body.append(container)
  const root = createRoot(container)
  let width = 500
  const bounds = vi
    .spyOn(HTMLElement.prototype, 'getBoundingClientRect')
    .mockImplementation(function (this: HTMLElement) {
      return {
        left: this.classList.contains('ui-tab') ? 210 : 0,
        right: this.classList.contains('ui-tab') ? 280 : width,
      } as DOMRect
    })
  try {
    await act(async () =>
      root.render(
        <Tabs
          id="service"
          label="Service sections"
          variant="section"
          activeId="domains"
          onSelect={() => {}}
          tabs={[
            { id: 'overview', label: 'Overview' },
            { id: 'domains', label: 'Domains' },
          ]}
        />,
      ),
    )
    const list = container.querySelector<HTMLDivElement>('[role=tablist]')!
    expect(list.scrollLeft).toBe(0)
    width = 200
    await act(async () => window.dispatchEvent(new Event('resize')))
    expect(list.scrollLeft).toBe(80)
    await act(async () => root.unmount())
    bounds.mockClear()
    window.dispatchEvent(new Event('resize'))
    expect(bounds).not.toHaveBeenCalled()
  } finally {
    bounds.mockRestore()
    container.remove()
  }
})
