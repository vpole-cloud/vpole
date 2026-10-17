import { expect, it } from 'vitest'
import { consumeRuntimeLogFrames } from './runtimeLogs'
import type { RuntimeLogFrame } from '../types'

const encode = new TextEncoder()
const wire = (frames: unknown[]) =>
  encode.encode(frames.map((frame) => JSON.stringify(frame) + '\n').join(''))
const opened = { kind: 'Opened', outputBytes: 0 }
const ended = (outputBytes = 0) => ({ kind: 'Ended', outputBytes, reason: 'StreamEnded' })
const stream = (chunks: Uint8Array[]) =>
  new ReadableStream<Uint8Array>({
    start(controller) {
      for (const chunk of chunks) controller.enqueue(chunk)
      controller.close()
    },
  })

it('handles coalesced frames larger than the old chunk/frame limit exactly like separate chunks', async () => {
  const frames = [
    opened,
    ...Array.from({ length: 160 }, (_, index) => ({
      kind: 'Output',
      text: 'x'.repeat(2048),
      outputBytes: (index + 1) * 2048,
    })),
    ended(160 * 2048),
  ]
  for (const chunks of [[wire(frames)], frames.map((frame) => wire([frame]))]) {
    const received: RuntimeLogFrame[] = []
    await consumeRuntimeLogFrames(stream(chunks), (frame) => received.push(frame))
    expect(received).toEqual(frames)
  }
})
it('decodes split UTF-8 and newline boundaries without changing output', async () => {
  const frames = [opened, { kind: 'Output', outputBytes: 6, text: 'Ї🚀' }, ended(6)]
  const bytes = wire(frames)
  const received: RuntimeLogFrame[] = []
  await consumeRuntimeLogFrames(stream([...bytes].map((byte) => new Uint8Array([byte]))), (frame) =>
    received.push(frame),
  )
  expect(received).toEqual(frames)
})
it.each([
  [encode.encode('x'.repeat(128 * 1024 + 1)), 'frame limit'],
  [encode.encode('x'.repeat(128 * 1024 + 1) + '\n'), 'frame limit'],
  [encode.encode('{invalid}\n'), 'interrupted'],
  [wire([opened]), 'without a completion marker'],
  [wire([opened, { kind: 'Output', text: 'x', outputBytes: 4 * 1024 * 1024 + 1 }]), 'read limit'],
  [wire([opened, opened]), 'changed unexpectedly'],
])('rejects invalid or unbounded protocol input: case %#', async (bytes, message) => {
  await expect(consumeRuntimeLogFrames(stream([bytes]), () => {})).rejects.toThrow(message)
})
it('cancels and releases the reader when aborted while waiting for a frame', async () => {
  const controller = new AbortController()
  let cancelled = false
  const body = new ReadableStream<Uint8Array>({
    cancel() {
      cancelled = true
    },
  })
  const reading = consumeRuntimeLogFrames(body, () => {}, controller.signal)
  controller.abort()
  await expect(reading).rejects.toMatchObject({ name: 'AbortError' })
  expect(cancelled).toBe(true)
  expect(body.locked).toBe(false)
})
