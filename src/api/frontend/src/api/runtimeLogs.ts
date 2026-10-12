import type { RuntimeLogFrame } from '../types'

export const runtimeReasons = new Set(['Unsupported', 'OwnershipNotVerified', 'OwnershipChanged', 'InstanceRemoved', 'InstanceReplaced', 'ContainerUnavailable',
  'TargetUnavailable', 'Forbidden', 'LogsUnavailable', 'CapacityBusy', 'Superseded', 'Timeout', 'LifetimeLimit', 'OutputLimit', 'EndOfWindow', 'StreamEnded'])

const frameLimit = 128 * 1024
const wireLimit = 32 * 1024 * 1024
const outputLimit = 4 * 1024 * 1024

// Limits apply to protocol frames and total data, independently of network buffering.
export async function consumeRuntimeLogFrames(body: ReadableStream<Uint8Array>, onFrame: (frame: RuntimeLogFrame) => void, signal?: AbortSignal) {
  const reader = body.getReader()
  const decoder = new TextDecoder()
  const aborted = () => { void reader.cancel().catch(() => {}) }
  signal?.addEventListener('abort', aborted, { once: true })
  let pending = ''; let opened = false; let bytes = 0; let frames = 0; let wireBytes = 0
  try {
    while (true) {
      if (signal?.aborted) throw new DOMException('Aborted', 'AbortError')
      const chunk = await reader.read().catch(() => {
        if (signal?.aborted) throw new DOMException('Aborted', 'AbortError')
        throw new Error('The runtime log connection was interrupted. Reconnect with a new recent window.')
      })
      if (signal?.aborted) throw new DOMException('Aborted', 'AbortError')
      wireBytes += chunk.value?.byteLength ?? 0
      if (wireBytes > wireLimit) throw new Error('The runtime log response exceeded the read limit.')
      pending += decoder.decode(chunk.value, { stream: !chunk.done })
      let offset = 0
      let end: number
      while ((end = pending.indexOf('\n', offset)) >= 0) {
        const line = pending.slice(offset, end); offset = end + 1
        if (line.length > frameLimit) throw new Error('The runtime log response exceeded the frame limit.')
        if (!line.trim()) continue
        let frame: RuntimeLogFrame
        try { frame = JSON.parse(line) as RuntimeLogFrame } catch { throw new Error('The runtime log response was interrupted. Reconnect with a new recent window.') }
        if (!frame || ++frames > 65536 || !Number.isInteger(frame.outputBytes) || frame.outputBytes < bytes || frame.outputBytes > outputLimit) throw new Error('The runtime log response exceeded the read limit.')
        if (frame.kind === 'Opened' && !opened && frame.outputBytes === 0) opened = true
        else if (frame.kind === 'Output' && opened && typeof frame.text === 'string' && frame.text.length > 0 && frame.text.length <= 16384) { /* Plain text only. */ }
        else if (frame.kind === 'Ended' && typeof frame.reason === 'string' && runtimeReasons.has(frame.reason)) { onFrame(frame); return }
        else throw new Error('The runtime log response changed unexpectedly. Reconnect with a new recent window.')
        bytes = frame.outputBytes; onFrame(frame)
      }
      pending = pending.slice(offset)
      if (pending.length > frameLimit) throw new Error('The runtime log response exceeded the frame limit.')
      if (chunk.done) throw new Error('The runtime log connection ended without a completion marker. Reconnect with a new recent window.')
    }
  } finally {
    signal?.removeEventListener('abort', aborted)
    await reader.cancel().catch(() => {})
    reader.releaseLock()
  }
}
