import { useEffect, useId, useRef, useState } from 'react'
import { readResourceRuntimeLogs, RuntimeLogUnavailableError } from '../api'
import { fetchResourceDiagnostics } from '../modules/operations/api'
import { useScopedQuery } from '../data/useScopedQuery'
import { useApiScope } from '../data/useApiScope'
import { useVisibleRefresh } from '../hooks/useVisibleRefresh'
import { useAppServices } from '../shell/AppContext'
import type { ResourceDeploymentOperation } from '../types'
import { Button, EmptyState, Notice } from './components'

const reasons: Record<string, string> = {
  Unsupported: 'This provider does not support runtime diagnostics.', OwnershipNotVerified: 'Workload ownership could not be verified.',
  OwnershipChanged: 'Workload ownership changed. Refresh instances before reading again.', InstanceRemoved: 'The selected instance no longer exists.',
  InstanceReplaced: 'The selected instance was replaced. Refresh and explicitly select its replacement.', ContainerUnavailable: 'The selected container is unavailable.',
  TargetUnavailable: 'This target could not be reached. Other targets can be inspected independently.', Forbidden: 'Target credentials cannot read these diagnostics.',
  LogsUnavailable: 'Output is unavailable for this container. It may not have started, or previous output may no longer exist.',
  CapacityBusy: 'All runtime log readers are busy. Close another log view and reconnect.', Superseded: 'This deployment changed. Refresh operations and select its current instances.',
  Timeout: 'The target read timed out.', LifetimeLimit: 'The stream lifetime limit was reached. Reconnect to a new recent window.',
  OutputLimit: 'The server output limit was reached. Reconnect to a new recent window.', EndOfWindow: 'This recent log window ended. A new read may overlap earlier output.',
  StreamEnded: 'The follow connection ended. Reconnect explicitly to a new recent window.',
}
export function ResourceRuntimeDiagnostics({ operation, active }: { operation: ResourceDeploymentOperation; active: boolean }) {
  const { api } = useAppServices()
  const deployment = operation.deployment
  const { identity } = useApiScope(api)
  const scope = `${identity}:${deployment.id}:${deployment.resourceType}:${deployment.resourceId}:${deployment.placement.deploymentTargetId}:${deployment.placement.deploymentRevision}:${deployment.placement.desiredGeneration}`
  return <DiagnosticsToggle key={scope} operation={operation} active={active} />
}
function DiagnosticsToggle({ operation, active }: { operation: ResourceDeploymentOperation; active: boolean }) {
  const [open, setOpen] = useState(false)
  return <div className="runtime-diagnostics">
    <Button onClick={() => setOpen(value => !value)}>{open ? 'Close runtime diagnostics' : 'Runtime logs and events'}</Button>
    {active && open && <DiagnosticsContent operation={operation} />}
  </div>
}
type LogState = { text: string; status: 'idle' | 'opening' | 'reading' | 'ended' | 'error'; reason: string; discarded: boolean; bytes: number }
const initial = (): LogState => ({ text: '', status: 'idle', reason: '', discarded: false, bytes: 0 })
function DiagnosticsContent({ operation }: { operation: ResourceDeploymentOperation }) {
  const fields = useId()
  const { api } = useAppServices()
  const deployment = operation.deployment
  const apiScope = useApiScope(api)
  const details = useScopedQuery({ scope: [api.baseUrl, api.tenantId], key: ['runtime-discovery', deployment.id, deployment.resourceType, deployment.resourceId, deployment.placement.deploymentTargetId, deployment.placement.deploymentRevision, deployment.placement.desiredGeneration],
    load: signal => fetchResourceDiagnostics(api.baseUrl, deployment.id, apiScope.credential(), api.tenantId, signal) })
  useVisibleRefresh(true, details.refresh)
  const [uid, setUid] = useState(''); const [container, setContainer] = useState(''); const [previous, setPrevious] = useState(false)
  const [logs, setLogs] = useState<LogState>(initial)
  const current = useRef<AbortController | null>(null)
  const alive = useRef(true)
  const heading = useRef<HTMLHeadingElement>(null)
  useEffect(() => { alive.current = true; heading.current?.focus(); return () => { alive.current = false; current.current?.abort() } }, [])
  const value = details.resource
  const exact = value && value.deploymentId === deployment.id && value.resourceType === deployment.resourceType && value.resourceId === deployment.resourceId &&
    value.targetId === deployment.placement.deploymentTargetId && value.deploymentRevision === deployment.placement.deploymentRevision && value.desiredGeneration === deployment.placement.desiredGeneration
  const diagnostics = exact && value.diagnostics.kind === 'Available' ? value.diagnostics : null
  const instance = diagnostics?.instances.find(item => item.uid === uid)
  const selectedContainer = instance?.containers.find(item => item.name === container)
  const stop = () => { current.current?.abort(); current.current = null }
  const reset = () => { stop(); setLogs(initial()) }
  const start = (follow: boolean) => {
    if (!exact || !instance || !selectedContainer || details.status !== 'success') return
    stop(); const owner = new AbortController(); current.current = owner
    setLogs({ ...initial(), status: 'opening' })
    void readResourceRuntimeLogs(api.baseUrl, deployment.id, { instanceUid: uid, container, expectedRevision: value.deploymentRevision, follow, previous, tailLines: 100 }, frame => {
      if (!alive.current || !apiScope.isCurrent() || owner.signal.aborted || current.current !== owner) return
      setLogs(old => {
        let text = frame.kind === 'Output' ? old.text + frame.text : old.text
        let discarded = old.discarded
        if (text.length > 64 * 1024) { text = text.slice(-64 * 1024); if (text.charCodeAt(0) >= 0xDC00 && text.charCodeAt(0) <= 0xDFFF) text = text.slice(1); discarded = true }
        return { text, discarded, bytes: frame.outputBytes, status: frame.kind === 'Ended' ? 'ended' : 'reading', reason: frame.kind === 'Ended' ? reasons[frame.reason!] : '' }
      })
    }, apiScope.credential(), api.tenantId, owner.signal).catch(failure => {
      if (!alive.current || !apiScope.isCurrent() || owner.signal.aborted || current.current !== owner) return
      setLogs(old => ({ ...old, status: 'error', reason: failure instanceof RuntimeLogUnavailableError ? reasons[failure.reason] ?? failure.message : failure instanceof Error ? failure.message : 'Runtime log read failed.' }))
    }).finally(() => { if (current.current === owner) current.current = null })
  }
  return <section className="panel runtime-diagnostics-viewer" aria-label="Runtime logs and Kubernetes events">
    <h3 ref={heading} tabIndex={-1} className="panel__title">Runtime diagnostics · {operation.targetDisplayName ?? 'Selected target'}</h3>
    <p className="field__hint">Instance output and Kubernetes warning events are separate from reconciliation, readiness and audit history.</p>
    <Button disabled={details.status === 'loading'} onClick={() => { reset(); setUid(''); setContainer(''); setPrevious(false); void details.refresh() }}>Refresh instances and events</Button>
    {details.error && <Notice tone="error">{details.error} Previous discovery is stale; refresh before opening another read.</Notice>}
    {!value && <EmptyState title={details.status === 'error' ? 'Runtime discovery unavailable.' : 'Discovering owned instances…'} />}
    {value && !exact && <Notice tone="error">This deployment changed. Refresh operations before opening runtime diagnostics.</Notice>}
    {exact && value.diagnostics.kind === 'Unavailable' && <Notice>{reasons[value.diagnostics.reason] ?? 'Runtime diagnostics are unavailable.'}</Notice>}
    {diagnostics && <>
      {diagnostics.instancesTruncated && <Notice>Instance discovery is limited. Only listed, verified instances can be selected.</Notice>}
      {diagnostics.instances.length === 0 ? <EmptyState title="No owned instances are currently available." /> : <div className="form-grid">
        <label className="field"><span className="field__label" id={`${fields}-instance`}>Instance</span><select aria-labelledby={`${fields}-instance`} className="field__control" value={uid} onChange={event => { reset(); setUid(event.target.value); setContainer(''); setPrevious(false) }}><option value="">Select an instance</option>
          {diagnostics.instances.map(item => <option key={item.uid} value={item.uid}>{item.name} · {item.phase}{item.currentGeneration ? '' : ' · earlier generation'}</option>)}</select></label>
        <label className="field"><span className="field__label" id={`${fields}-container`}>Container</span><select aria-labelledby={`${fields}-container`} className="field__control" value={container} disabled={!instance} onChange={event => { reset(); setContainer(event.target.value); setPrevious(false) }}><option value="">Select a container</option>
          {instance?.containers.map(item => <option key={item.name} value={item.name}>{item.name} · {item.kind}</option>)}</select></label>
      </div>}
      {instance && <p className="field__hint">{instance.ready ? 'Observed ready' : 'Not ready'} · {instance.restartCount} restarts · {instance.reasons.join(', ') || 'No warning reason observed'}.</p>}
      {instance?.containersTruncated && <Notice>Container discovery is limited. Only listed containers can be selected.</Notice>}
      <label className="field field--checkbox"><input type="checkbox" checked={previous} disabled={!selectedContainer?.previousAvailable} onChange={event => { reset(); setPrevious(event.target.checked) }} />Previous terminated container output</label>
      <div className="row-actions"><Button disabled={!selectedContainer || details.status !== 'success'} onClick={() => start(false)}>{logs.status === 'ended' || logs.status === 'error' ? 'Reconnect recent logs' : 'Read recent logs'}</Button>
        <Button disabled={!selectedContainer || previous || details.status !== 'success'} onClick={() => start(true)}>Follow runtime logs</Button>
        {(logs.status === 'opening' || logs.status === 'reading') && <Button onClick={() => { stop(); setLogs(old => ({ ...old, status: 'ended', reason: 'Reading stopped. The workload continues running.' })) }}>Stop reading</Button>}
      </div>
      <p className="field__hint">Every read starts a new recent window of up to 100 lines. Reconnection can overlap output; runtime logs are not archived. This view retains at most 65,536 characters.</p>
      {(logs.status === 'opening' || logs.status === 'reading') && <p role="status">{logs.status === 'opening' ? 'Opening runtime logs…' : `Reading runtime logs · ${logs.bytes.toLocaleString()} bytes…`}</p>}
      {logs.reason && <Notice tone={logs.status === 'error' ? 'error' : 'info'}>{logs.reason}</Notice>}
      {logs.discarded && <Notice>Earlier output left this bounded view. Server limits and Kubernetes log retention are separate.</Notice>}
      {logs.text && <pre className="build-log-output" aria-label="Runtime log output">{logs.text}</pre>}
      {logs.status === 'ended' && !logs.text && <Notice>No output was available in this window.</Notice>}
      <p className="field__hint">Workload output may contain sensitive information. It is displayed as plain text and is cleared when the view closes or the tenant changes.</p>
      <h4>Kubernetes warning events</h4>
      <p className="field__hint">A bounded sample for verified native object UIDs, ordered within this sample. Raw messages and workload configuration are excluded.</p>
      {diagnostics.eventsUnavailableReason && <Notice>{reasons[diagnostics.eventsUnavailableReason] ?? 'Kubernetes events are unavailable.'}</Notice>}
      {diagnostics.eventsTruncated && <Notice>The event sample is limited; it does not cover all workload objects or all retained events.</Notice>}
      {diagnostics.events.map((event, index) => <p className="field__hint" key={`${event.objectName}:${event.reason}:${index}`}><code>{event.objectKind} {event.objectName}</code> · {event.reason} · {event.count} occurrences{event.lastSeenAtUtc && ` · ${new Date(event.lastSeenAtUtc).toLocaleString()}`}</p>)}
      {!diagnostics.events.length && !diagnostics.eventsUnavailableReason && <p>No warnings were found in this bounded sample.</p>}
    </>}
  </section>
}
