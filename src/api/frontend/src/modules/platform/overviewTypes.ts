// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import type { ResourceGroup, WebService } from '../../types'

export interface SearchHit {
  resourceTypeId: string
  sourceType: string
  sourceId: string
  title: string
  resourceGroupId?: string | null
  resourceGroupName?: string | null
  operationalStatus?: { status: string; failureCode?: string | null } | null
}

export interface OverviewData {
  resourceGroups: ResourceGroup[]
  webServices: WebService[]
}
