// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { readResourceRoute, readWorkspace as readStoredWorkspace } from '../../shell/workspaceState'
import type { ResourceWorkspace } from '../../shell/workspaceState'
export const groupSections = ['resources', 'operations', 'configuration'] as const
export type GroupSection = (typeof groupSections)[number]
export type GroupWorkspace = ResourceWorkspace<GroupSection>
export const readGroupRoute = (search: string) =>
  readResourceRoute(search, groupSections, 'resources')
export const readWorkspace = (storageKey: string) =>
  readStoredWorkspace(storageKey, groupSections, 'resources')
