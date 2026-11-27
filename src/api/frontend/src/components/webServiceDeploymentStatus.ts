// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

export const retryOperationForStatus = (status: string): 'Apply' | 'Delete' =>
  status.toLowerCase().includes('delete') ? 'Delete' : 'Apply'
