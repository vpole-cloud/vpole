// Copyright (C) 2026 Victor Shnaider and vPole contributors
// SPDX-License-Identifier: AGPL-3.0-only

import { buildHeaders, request } from './transport'
import { consumeRuntimeLogFrames, runtimeReasons } from './runtimeLogs'

export class RuntimeLogUnavailableError extends Error {
  readonly reason: string
  constructor(reason: string) {
    super('Runtime logs are unavailable. Refresh instances and start a new recent window.')
    this.reason = reason
  }
}

export async function readResourceRuntimeLogs(
  base: string,
  deploymentId: string,
  selection: import('../types').RuntimeLogSelection,
  onFrame: (frame: import('../types').RuntimeLogFrame) => void,
  token?: string,
  tenantId?: string,
  signal?: AbortSignal,
) {
  const query = new URLSearchParams(
    Object.entries(selection).map(([key, value]) => [key, String(value)]),
  )
  const response = await request(
    `${base}/api/v1/resource-deployments/${encodeURIComponent(deploymentId)}/runtime-logs?${query}`,
    { headers: buildHeaders(token, tenantId), signal },
  ).catch(() => {
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError')
    throw new Error('Runtime logs could not be reached. Reconnect with a new recent window.')
  })
  if (signal?.aborted) {
    await response.body?.cancel().catch(() => {})
    throw new DOMException('Aborted', 'AbortError')
  }
  if (!response.ok) {
    if (response.status === 409) {
      let reason = 'LogsUnavailable'
      try {
        const body = (await response.json()) as { reason?: string }
        if (body.reason && runtimeReasons.has(body.reason)) reason = body.reason
      } catch {
        /* Keep fixed error text. */
      }
      throw new RuntimeLogUnavailableError(reason)
    }
    throw new Error(
      response.status === 403
        ? 'Your tenant role cannot read these logs.'
        : response.status === 404
          ? 'This deployment is unavailable in the active tenant.'
          : response.status === 400
            ? 'The instance selection changed or is invalid. Refresh instances.'
            : 'Runtime logs could not be reached. Reconnect with a new recent window.',
    )
  }
  if (!response.headers.get('Content-Type')?.startsWith('application/x-ndjson') || !response.body)
    throw new Error('The runtime log response is unavailable. Reconnect with a new recent window.')
  return consumeRuntimeLogFrames(response.body, onFrame, signal)
}
