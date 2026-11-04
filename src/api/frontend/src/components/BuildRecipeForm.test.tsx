// @vitest-environment jsdom
import { act } from 'react'
import { createRoot } from 'react-dom/client'
import { expect, it, vi } from 'vitest'
import { ApiError } from '../api'
import { BuildRecipeForm } from './BuildRecipeForm'
import type { SourceBuildConfiguration } from '../types'

const configuration: SourceBuildConfiguration = {
  id: 'configuration',
  technicalName: 'api-build',
  revision: 1,
  sourceRepositoryId: 'source',
  webServiceId: 'service',
  createdAtUtc: '',
  updatedAtUtc: '',
  createdByUserId: 'user',
  recipe: {
    dockerfile: 'FROM scratch',
  },
}

it('preserves a dirty recipe across refresh and field failure, then records the successful normalized recipe', async () => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  Object.defineProperties(HTMLDialogElement.prototype, {
    showModal: {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = true
      },
    },
    close: {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = false
      },
    },
  })
  const container = document.createElement('div')
  const mountedRoot = createRoot(container)
  const onSave = vi
    .fn()
    .mockRejectedValueOnce(
      new ApiError('Invalid recipe', 400, { 'Body.Recipe.Dockerfile': ['Dockerfile is invalid.'] }),
    )
    .mockImplementationOnce(async (recipe) => ({
      ...configuration,
      recipe,
    }))
  const onDirtyChange = vi.fn()
  const render = (value = configuration) =>
    mountedRoot.render(
      <BuildRecipeForm
        configuration={value}
        canManage
        busy={false}
        onSave={onSave}
        onDirtyChange={onDirtyChange}
      />,
    )
  try {
    await act(async () => render())
    const dockerfile = container.querySelector<HTMLTextAreaElement>('.build-recipe__dockerfile')!
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!.call(
        dockerfile,
        'FROM busybox\nCMD ["true"]',
      )
      dockerfile.dispatchEvent(new Event('input', { bubbles: true }))
    })
    expect(onDirtyChange).toHaveBeenLastCalledWith(true)
    await act(async () =>
      render({
        ...configuration,
        recipe: { ...configuration.recipe, dockerfile: 'FROM refreshed' },
      }),
    )
    expect(dockerfile.value).toContain('FROM busybox')
    expect(
      [...container.querySelectorAll('button')].find(
        (button) => button.textContent === 'Save build recipe',
      )!.disabled,
    ).toBe(true)
    await act(async () =>
      [...container.querySelectorAll('button')]
        .find((button) => button.textContent === 'Review saved recipe')!
        .click(),
    )
    expect(container.querySelector('dialog')?.textContent).toContain('Current Dockerfile')
    await act(async () =>
      [...container.querySelectorAll('button')]
        .find((button) => button.textContent === 'Keep draft for this recipe')!
        .click(),
    )
    await act(async () =>
      container
        .querySelector('form')!
        .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
    )
    expect(container.textContent).toContain('Dockerfile is invalid.')
    expect(dockerfile.value).toContain('FROM busybox')
    await act(async () =>
      container
        .querySelector('form')!
        .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
    )
    expect(onSave.mock.calls[1][0]).toEqual(
      expect.objectContaining({
        dockerfile: 'FROM busybox\nCMD ["true"]',
      }),
    )
    expect(onDirtyChange).toHaveBeenLastCalledWith(false)
  } finally {
    await act(async () => mountedRoot.unmount())
  }
})

it('preserves a draft but refuses form submission after tenant access becomes read-only', async () => {
  Object.assign(globalThis, { IS_REACT_ACT_ENVIRONMENT: true })
  Object.defineProperties(HTMLDialogElement.prototype, {
    showModal: {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = true
      },
    },
    close: {
      configurable: true,
      value: function (this: HTMLDialogElement) {
        this.open = false
      },
    },
  })
  const container = document.createElement('div')
  const mountedRoot = createRoot(container)
  const onSave = vi.fn()
  const onDirtyChange = vi.fn()
  const render = (canManage: boolean) =>
    mountedRoot.render(
      <BuildRecipeForm
        configuration={configuration}
        canManage={canManage}
        busy={false}
        onSave={onSave}
        onDirtyChange={onDirtyChange}
      />,
    )
  try {
    await act(async () => render(true))
    const dockerfile = container.querySelector<HTMLTextAreaElement>('.build-recipe__dockerfile')!
    await act(async () => {
      Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')!.set!.call(
        dockerfile,
        'FROM busybox',
      )
      dockerfile.dispatchEvent(new Event('input', { bubbles: true }))
    })
    await act(async () => render(false))
    expect(dockerfile.readOnly).toBe(true)
    expect(dockerfile.value).toBe('FROM busybox')
    expect(container.querySelector('button[type="submit"]')).toBeNull()
    await act(async () =>
      container
        .querySelector('form')!
        .dispatchEvent(new Event('submit', { bubbles: true, cancelable: true })),
    )
    expect(onSave).not.toHaveBeenCalled()
  } finally {
    await act(async () => mountedRoot.unmount())
  }
})
