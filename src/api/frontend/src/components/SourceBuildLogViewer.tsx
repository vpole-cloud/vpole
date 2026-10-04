import { useCallback, useEffect, useRef, useState } from 'react'
import { fetchSourceBuildJob, fetchSourceBuildLogs } from '../api'
import { useResource } from '../hooks/useResource'
import { useVisibleRefresh } from '../hooks/useVisibleRefresh'
import { useAppServices } from '../shell/AppContext'
import type { SourceBuildJob, SourceBuildLogEntry, SourceBuildLogs } from '../types'
import { Button, EmptyState, Notice, StatusBadge } from '../ui/components'

export function SourceBuildLogViewer({ jobId, webServiceId, onClose }: { jobId: string; webServiceId: string; onClose: () => void }) {
  const { api } = useAppServices()
  const scope = `${api.baseUrl}:${api.tenantId}:${api.token}:${webServiceId}:${jobId}`
  return <BuildLogIdentity key={scope} scope={scope} jobId={jobId} webServiceId={webServiceId} onClose={onClose} />
}

function BuildLogIdentity({ scope, jobId, webServiceId, onClose }: { scope: string; jobId: string; webServiceId: string; onClose: () => void }) {
  const { api } = useAppServices()
  const job = useResource(scope, signal => fetchSourceBuildJob(api.baseUrl, jobId, api.token, api.tenantId, signal))
  useVisibleRefresh(!job.resource || !['failed', 'succeeded'].includes(job.resource.state.status), job.refresh, 5000)
  if (job.resource?.webServiceId === webServiceId) return <BuildLogCapture job={job.resource} onClose={onClose} />
  return <section className="panel"><div className="panel__header"><h2 className="panel__title">Build logs</h2><Button onClick={onClose}>Close logs</Button></div>
    {job.error || job.resource ? <Notice tone="error">This build is unavailable for the selected service and tenant.<Button onClick={() => void job.refresh()}>Retry build read</Button></Notice> : <EmptyState title="Loading build identity…" />}</section>
}

type ViewState = { entries: SourceBuildLogEntry[]; metadata: SourceBuildLogs | null; status: 'loading' | 'success' | 'error'; error: string; discardedThrough: number }
const initial = (): ViewState => ({ entries: [], metadata: null, status: 'loading', error: '', discardedThrough: 0 })
const byteLength = (entries: SourceBuildLogEntry[]) => entries.reduce((total, entry) => total + new TextEncoder().encode(entry.text).length, 0)

function BuildLogCapture({ job, onClose }: { job: SourceBuildJob; onClose: () => void }) {
  const { api } = useAppServices()
  const { baseUrl, token, tenantId } = api
  const { id: jobId, webServiceId } = job
  const [state, setState] = useState<ViewState>(initial)
  const [follow, setFollow] = useState(true)
  const lifetime = useRef<AbortController | null>(null)
  const currentRead = useRef<AbortController | null>(null)
  const cursor = useRef(0)
  const heading = useRef<HTMLHeadingElement>(null)
  const read = useCallback(async () => {
    const owner = lifetime.current
    if (!owner || owner.signal.aborted || currentRead.current) return
    const request = new AbortController(); currentRead.current = request
    const signal = AbortSignal.any([owner.signal, request.signal])
    setState(value => ({ ...value, status: 'loading', error: '' }))
    try {
      const page = await fetchSourceBuildLogs(baseUrl, jobId, webServiceId, cursor.current, token, tenantId, signal)
      if (signal.aborted) return
      if (page.buildJobId !== jobId || page.webServiceId !== webServiceId) throw new Error('The returned build identity changed. Restart this log view.')
      let next = cursor.current
      for (const entry of page.entries) {
        if (entry.sequence !== next + 1) throw new Error('The retained log range changed. Restart this log view.')
        next = entry.sequence
      }
      if (next !== page.nextSequence || page.hasMore && next === cursor.current) throw new Error('The returned log cursor changed. Restart this log view.')
      cursor.current = next
      setState(value => {
        const entries = page.captureState === 'Expired' ? [] : [...value.entries, ...page.entries]
        let size = byteLength(entries)
        let discardedThrough = value.discardedThrough
        while (entries.length > 512 || size > 256 * 1024) {
          const removed = entries.shift()!; size -= new TextEncoder().encode(removed.text).length; discardedThrough = removed.sequence
        }
        return { entries, metadata: page, status: 'success', error: '', discardedThrough }
      })
    } catch (failure) {
      if (!signal.aborted) setState(value => ({ ...value, status: 'error', error: failure instanceof Error ? failure.message : 'Build log read failed.' }))
    } finally { if (currentRead.current === request) currentRead.current = null }
  }, [baseUrl, jobId, webServiceId, token, tenantId])
  useEffect(() => {
    const owner = new AbortController(); lifetime.current = owner
    queueMicrotask(() => { if (!owner.signal.aborted) { heading.current?.focus(); void read() } })
    return () => { owner.abort(); currentRead.current?.abort() }
  }, [read])
  const metadata = state.metadata
  const terminalJob = job.state.status === 'failed' || job.state.status === 'succeeded'
  const awaitingCapture = !metadata || metadata.captureState === 'Open' || metadata.hasMore || metadata.captureState === 'Unavailable' && !terminalJob
  useVisibleRefresh(follow && awaitingCapture && state.status !== 'error', read, 2000)
  useEffect(() => { if (follow && state.status === 'success' && metadata?.hasMore) queueMicrotask(() => void read()) }, [follow, state.status, metadata?.hasMore, metadata?.nextSequence, read])
  const restart = () => { currentRead.current?.abort(); currentRead.current = null; cursor.current = 0; setState(initial()); void read() }
  return <section className="panel build-log-viewer" aria-label={`Build logs for ${jobId}`}>
    <div className="panel__header"><div><h2 className="panel__title" ref={heading} tabIndex={-1}>Build logs</h2><p className="field__hint">Build <code>{jobId}</code> · {job.branch} · commit <code>{job.commitSha.slice(0, 10)}</code></p></div>
      <div className="row-actions"><StatusBadge tone={job.state.status === 'failed' ? 'danger' : 'info'}>{job.state.status}</StatusBadge><Button onClick={onClose}>Close logs</Button></div></div>
    <p className="field__hint">Capture is independent of build and deployment status. Closing or pausing this viewer leaves the build running.</p>
    <div className="row-actions"><Button onClick={() => setFollow(value => !value)}>{follow ? 'Pause follow' : 'Follow logs'}</Button>
      <Button disabled={state.status === 'loading'} onClick={() => void read()}>{state.status === 'error' ? 'Reconnect logs' : metadata?.hasMore ? 'Load next log range' : 'Refresh logs'}</Button>
      <Button onClick={restart}>Restart log view</Button></div>
    {state.error && <Notice tone="error">{state.error} Previously displayed output is retained; reconnect resumes after sequence {cursor.current}.</Notice>}
    {metadata && <p className="field__hint">Capture {metadata.captureState} · displayed through sequence {cursor.current} of {metadata.lastSequence} · {metadata.capturedBytes.toLocaleString()} captured bytes{metadata.maximumBytes != null && ` / ${metadata.maximumBytes.toLocaleString()} maximum`}{metadata.expiresAtUtc && ` · retained until ${new Date(metadata.expiresAtUtc).toLocaleString()}`}.</p>}
    {metadata?.truncated && <Notice>The server capture limit was reached. This is a retained prefix; later output was discarded while the process continued.</Notice>}
    {metadata?.captureState === 'Interrupted' && <Notice>The capture worker stopped renewing its lease. Saved output remains available; this does not resume the build.</Notice>}
    {metadata?.captureState === 'Expired' && <Notice>The log retention period expired. Output is no longer available.</Notice>}
    {metadata?.captureState === 'Unavailable' && <Notice>Capture is unavailable or has not started. Any saved prefix is shown; inspect the job's separate status.</Notice>}
    {state.discardedThrough > 0 && <Notice>This viewer keeps a bounded tail. Entries through sequence {state.discardedThrough} left this view; server retention is separate. Restart the view to read from the retained beginning.</Notice>}
    {state.status === 'loading' && <p role="status">Fetching the next log range…</p>}
    {state.entries.length > 0 ? <pre className="build-log-output" aria-label="Captured build output">{state.entries.map(entry => entry.text).join('')}</pre> : state.status !== 'loading' && metadata?.captureState !== 'Expired' && <EmptyState title="No captured output in this range." />}
    <p className="field__hint">Platform registry credentials are masked before storage. Application output may contain other sensitive information; this viewer renders it as plain text.</p>
  </section>
}
