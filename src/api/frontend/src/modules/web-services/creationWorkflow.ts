// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { WebServiceInput } from '../../types'

export interface SourceBuildCreation {
  sourceRepositoryId: string
  technicalName: string
  dockerfile: string
}

export type CreationCommand = Readonly<
  { input: WebServiceInput } & ({ mode: 'image' } | { mode: 'git'; source: SourceBuildCreation })
>
export interface AcceptedCreation {
  serviceId?: string
  configurationId?: string
  jobId?: string
}
export type CreationPhase = 'creating' | 'configuring' | 'queueing'
export type CreationState =
  | { kind: 'editing'; error?: unknown }
  | { kind: 'creating'; command: CreationCommand }
  | {
      kind: 'configuring'
      command: CreationCommand
      accepted: AcceptedCreation & { serviceId: string }
    }
  | {
      kind: 'queueing'
      command: CreationCommand
      accepted: AcceptedCreation & { serviceId: string; configurationId: string }
    }
  | {
      kind: 'completed'
      command: CreationCommand
      accepted: AcceptedCreation & { serviceId: string }
    }
  | {
      kind: 'recoverable-failure'
      phase: CreationPhase
      command: CreationCommand
      accepted: AcceptedCreation
      error: unknown
    }
export type CreationEvent =
  | { type: 'begin'; command: CreationCommand }
  | { type: 'created'; command: CreationCommand; serviceId: string }
  | { type: 'configured'; command: CreationCommand; serviceId: string; configurationId: string }
  | {
      type: 'completed'
      command: CreationCommand
      accepted: AcceptedCreation & { serviceId: string }
    }
  | {
      type: 'failed'
      command: CreationCommand
      phase: CreationPhase
      accepted: AcceptedCreation
      error: unknown
      rejected?: boolean
    }

export function creationReducer(_state: CreationState, event: CreationEvent): CreationState {
  switch (event.type) {
    case 'begin':
      return { kind: 'creating', command: event.command }
    case 'created':
      return {
        kind: 'configuring',
        command: event.command,
        accepted: { serviceId: event.serviceId },
      }
    case 'configured':
      return {
        kind: 'queueing',
        command: event.command,
        accepted: { serviceId: event.serviceId, configurationId: event.configurationId },
      }
    case 'completed':
      return { kind: 'completed', command: event.command, accepted: event.accepted }
    case 'failed':
      return event.rejected && !event.accepted.serviceId
        ? { kind: 'editing', error: event.error }
        : {
            kind: 'recoverable-failure',
            phase: event.phase,
            command: event.command,
            accepted: event.accepted,
            error: event.error,
          }
  }
}

// This command stays local to the editor; no credential or form payload enters a shared cache.
export function creationCommand(
  input: WebServiceInput,
  mode: 'image' | 'git',
  source?: SourceBuildCreation,
): CreationCommand {
  const snapshot = structuredClone(input)
  if (mode === 'git') {
    if (!source?.sourceRepositoryId) throw new Error('Choose a linked Git source first.')
    if (!/^[a-z](?:[a-z0-9-]{0,61}[a-z0-9])?$/.test(source.technicalName))
      throw new Error('Use a lowercase technical name up to 63 characters, starting with a letter.')
    if (!source.dockerfile.trim() || source.dockerfile.length > 100000)
      throw new Error('Provide an executable Dockerfile up to 100000 characters.')
    return {
      mode,
      input: { ...snapshot, creationMode: 'SourceBuild' },
      source: structuredClone(source),
    }
  }
  return { mode, input: { ...snapshot, creationMode: 'Image' } }
}
