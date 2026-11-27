// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { expect, it } from 'vitest'
import { creationCommand } from './creationWorkflow'
import type { WebServiceInput } from './types'

it('captures the intended build recipe and creation mode before any accepted writes', () => {
  const input = { name: 'application', imageName: 'previous-image-draft' } as WebServiceInput
  const source = {
    sourceRepositoryId: 'repository',
    technicalName: 'application-build',
    dockerfile: 'FROM scratch',
  }
  const command = creationCommand(input, 'git', source)
  source.dockerfile = 'FROM changed-after-submission'
  input.name = 'changed-after-submission'
  expect(command.input.name).toBe('application')
  expect(command.input.creationMode).toBe('SourceBuild')
  expect(command.mode === 'git' && command.source.dockerfile).toBe('FROM scratch')
})

it('rejects missing executable recipe and invalid technical identity before creating any resource', () => {
  const input = { name: 'application' } as WebServiceInput
  expect(() => creationCommand(input, 'git')).toThrow('Choose a linked Git source')
  expect(() =>
    creationCommand(input, 'git', {
      sourceRepositoryId: 'repository',
      technicalName: 'Invalid Name',
      dockerfile: 'FROM scratch',
    }),
  ).toThrow('technical name')
  expect(() =>
    creationCommand(input, 'git', {
      sourceRepositoryId: 'repository',
      technicalName: 'application',
      dockerfile: ' ',
    }),
  ).toThrow('Dockerfile')
})
