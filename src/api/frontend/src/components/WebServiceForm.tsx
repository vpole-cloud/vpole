import { useEffect, useState } from 'react'
import { Button, Notice } from '../console/components'
import { useIsolationScopes, type LoadIsolationScopes } from '../hooks/useIsolationScopes'
import type {
  AvailableComputeProfile,
  AvailableDeploymentTarget,
  ResourceGroup,
  WebService,
  WebServiceDeploymentInput,
  WebServiceInput,
} from '../types'

interface WebServiceFormProps {
  mode: 'create' | 'edit'
  resourceGroups: ResourceGroup[]
  initialValue?: WebService | null
  availableTargets: AvailableDeploymentTarget[]
  targetsStatus: 'idle' | 'loading' | 'success' | 'error'
  targetsError?: string
  computeProfilesByTarget: Record<string, AvailableComputeProfile[]>
  submitStatus: 'idle' | 'loading' | 'success' | 'error'
  submitError?: string
  onLoadTargets: (isolationScopeId?: string, requiresPublicIngress?: boolean, signal?: AbortSignal) => Promise<AvailableDeploymentTarget[]>
  onLoadComputeProfiles: (deploymentTargetId: string) => Promise<AvailableComputeProfile[]>
  onLoadIsolationScopes?: LoadIsolationScopes
  onSubmit: (input: WebServiceInput) => Promise<void>
  onCancelEdit?: () => void
  onDirtyChange?: (dirty: boolean) => void
}

const parseEnvironmentVariables = (value: string) =>
  value
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean)
    .map((line) => {
      const [key, ...rest] = line.split('=')
      return { key: key?.trim() ?? '', value: rest.join('=').trim() }
    })
    .filter((item) => item.key)

const deploymentConfiguration = (
  targetId: string,
  current?: WebServiceDeploymentInput,
): WebServiceDeploymentInput => ({
  deploymentTargetId: targetId,
  fixedReplicas: current?.fixedReplicas ?? 1,
  computeProfileId: current?.computeProfileId,
})

const fieldsFor = (service: WebService) => ({
  name: service.name,
  resourceGroupId: service.resourceGroupId,
  isolationScopeId: service.isolationScopeId ?? '',
  exposure: service.exposure.mode,
  imageServer: service.spec.image.server,
  imageName: service.spec.image.name,
  imageTag: service.spec.image.tag,
  probePath: service.spec.probes?.readiness.path ?? '/health',
  probePort: service.spec.probes?.readiness.port ?? '8080',
  servicePort: String(service.spec.config.portMappings[0]?.servicePort ?? 80),
  targetPort: service.spec.config.portMappings[0]?.targetPort ?? '8080',
  portName: service.spec.config.portMappings[0]?.name ?? 'http',
  environmentText: Object.entries(service.spec.config.environmentVariables).map(([key, value]) => `${key}=${value}`).join('\n'),
})

export function WebServiceForm({
  mode,
  resourceGroups,
  initialValue,
  availableTargets,
  targetsStatus,
  targetsError,
  computeProfilesByTarget,
  submitStatus,
  submitError,
  onLoadTargets,
  onLoadComputeProfiles,
  onLoadIsolationScopes,
  onSubmit,
  onCancelEdit,
  onDirtyChange,
}: WebServiceFormProps) {
  const [name, setName] = useState(initialValue?.name ?? '')
  const [resourceGroupId, setResourceGroupId] = useState(
    initialValue?.resourceGroupId ?? resourceGroups[0]?.id ?? '',
  )
  const [isolationScopeId, setIsolationScopeId] = useState(initialValue?.isolationScopeId ?? '')
  const scopes = useIsolationScopes(resourceGroupId, mode === 'create' ? onLoadIsolationScopes : undefined)
  const resolvedScopeId = isolationScopeId || scopes.items.find((scope) => scope.isDefault)?.id
  const unavailableScope = Boolean(isolationScopeId && !scopes.items.some((scope) => scope.id === isolationScopeId))
  const [exposure, setExposure] = useState<'Private' | 'Public'>(initialValue?.exposure.mode ?? 'Private')
  const [imageServer, setImageServer] = useState(initialValue?.spec.image.server ?? '')
  const [imageName, setImageName] = useState(initialValue?.spec.image.name ?? '')
  const [imageTag, setImageTag] = useState(initialValue?.spec.image.tag ?? 'latest')
  const [probePath, setProbePath] = useState(initialValue?.spec.probes?.readiness.path ?? '/health')
  const [probePort, setProbePort] = useState(initialValue?.spec.probes?.readiness.port ?? '8080')
  const [servicePort, setServicePort] = useState(
    String(initialValue?.spec.config.portMappings[0]?.servicePort ?? 80),
  )
  const [targetPort, setTargetPort] = useState(
    initialValue?.spec.config.portMappings[0]?.targetPort ?? '8080',
  )
  const [portName, setPortName] = useState(
    initialValue?.spec.config.portMappings[0]?.name ?? 'http',
  )
  const [environmentText, setEnvironmentText] = useState(
    Object.entries(initialValue?.spec.config.environmentVariables ?? {})
      .map(([key, value]) => `${key}=${value}`)
      .join('\n'),
  )
  const [selectedTargets, setTargets] = useState<WebServiceDeploymentInput[] | undefined>()
  const eligibleTargets = targetsStatus === 'success' ? availableTargets : []
  const defaultTargetId = mode === 'create' && eligibleTargets.length === 1 ? eligibleTargets[0].id : undefined
  const targets = selectedTargets ?? (defaultTargetId ? [deploymentConfiguration(defaultTargetId)] : [])
  const [showAdvancedTargets, setShowAdvancedTargets] = useState(false)
  const [validationError, setValidationError] = useState('')
  const snapshot = JSON.stringify({ name, resourceGroupId, isolationScopeId, exposure, imageServer,
    imageName, imageTag, probePath, probePort, servicePort, targetPort, portName, environmentText, selectedTargets })
  const [savedSnapshot, setSavedSnapshot] = useState(snapshot)
  const dirty = snapshot !== savedSnapshot
  const [loadedRevision, setLoadedRevision] = useState(initialValue?.activeRevision)
  // Adopt refreshed server values only when this form has no unsaved draft.
  if (initialValue && initialValue.activeRevision !== loadedRevision && !dirty) {
    const fields = fieldsFor(initialValue)
    setLoadedRevision(initialValue.activeRevision)
    setName(fields.name)
    setResourceGroupId(fields.resourceGroupId)
    setIsolationScopeId(fields.isolationScopeId)
    setExposure(fields.exposure)
    setImageServer(fields.imageServer)
    setImageName(fields.imageName)
    setImageTag(fields.imageTag)
    setProbePath(fields.probePath)
    setProbePort(fields.probePort)
    setServicePort(fields.servicePort)
    setTargetPort(fields.targetPort)
    setPortName(fields.portName)
    setEnvironmentText(fields.environmentText)
    setTargets(undefined)
    setValidationError('')
    setSavedSnapshot(JSON.stringify(fields))
  }
  useEffect(() => { onDirtyChange?.(dirty) }, [dirty, onDirtyChange])

  useEffect(() => {
    if (selectedTargets === undefined && defaultTargetId) {
      void onLoadComputeProfiles(defaultTargetId)
    }
  }, [defaultTargetId, onLoadComputeProfiles, selectedTargets])

  useEffect(() => {
    if (mode !== 'create' || scopes.status === 'loading') {
      return
    }
    const controller = new AbortController()
    void onLoadTargets(
      resolvedScopeId,
      exposure === 'Public',
      controller.signal,
    )
    return () => controller.abort()
  }, [exposure, resolvedScopeId, resourceGroupId, scopes.status, mode, onLoadTargets])

  const setTargetSelected = (targetId: string, selected: boolean) => {
    if (selected) void onLoadComputeProfiles(targetId)
    setTargets((value) => {
      const current = value ?? targets
      if (selected) {
        return [...current, deploymentConfiguration(targetId)]
      }
      return current.filter((target) => target.deploymentTargetId !== targetId)
    })
  }

  const updateTarget = (targetId: string, change: Partial<WebServiceDeploymentInput>) => {
    setTargets((current) => (current ?? targets).map((target) =>
      target.deploymentTargetId === targetId ? { ...target, ...change } : target,
    ))
  }

  const handleSubmit = async (event: React.FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!name.trim()) {
      setValidationError('Name is required.')
      return
    }
    if (!resourceGroupId) {
      setValidationError('Resource group is required.')
      return
    }
    if (mode === 'create' && (scopes.status === 'loading' || unavailableScope)) {
      setValidationError('Select an available isolation scope or use the group default.')
      return
    }
    if (!imageServer.trim() || !imageName.trim() || !imageTag.trim()) {
      setValidationError('Image registry, name, and tag are required.')
      return
    }
    if (mode === 'create' && targetsStatus !== 'success') {
      setValidationError('Wait for eligible deployment targets to load before creating the service.')
      return
    }
    if (mode === 'create' && targets.length === 0) {
      setValidationError('Select at least one eligible deployment target.')
      return
    }
    if (targets.some((target) => !Number.isInteger(target.fixedReplicas) || target.fixedReplicas < 1)) {
      setValidationError('Each target needs at least one fixed replica.')
      return
    }

    setValidationError('')
    try {
      await onSubmit({
        name: name.trim(),
        exposure,
        resourceGroupId,
        isolationScopeId: isolationScopeId.trim() || undefined,
        imageServer: imageServer.trim(),
        imageName: imageName.trim(),
        imageTag: imageTag.trim(),
        probePath: probePath.trim(),
        probePort: probePort.trim(),
        servicePort: Number(servicePort),
        targetPort: targetPort.trim(),
        portName: portName.trim(),
        environmentVariables: parseEnvironmentVariables(environmentText),
        autoscaling: initialValue?.spec.autoscaling ?? null,
        targets,
      })
      setSavedSnapshot(snapshot)
    } catch (error) {
      setValidationError(error instanceof Error ? error.message : 'Unable to save the service.')
    }
  }

  const exactlyOneTarget = eligibleTargets.length === 1

  return (
    <section className="panel web-service-form-panel">
      <div className="panel__header">
        <div>
          <p className="panel__eyebrow">{mode === 'create' ? 'Create' : 'Edit'}</p>
          <h2 className="panel__title">{mode === 'create' ? 'New web service' : 'Service configuration'}</h2>
        </div>
      </div>

      <form className="resource-group-form" onSubmit={(event) => void handleSubmit(event)}>
        <fieldset className="web-service-form__fields" disabled={submitStatus === 'loading'}>
        <label className="field">
          <span className="field__label">Name</span>
          <input className="field__control" disabled={mode === 'edit'} value={name} onChange={(event) => setName(event.target.value)} placeholder="pole-api" />
          {mode === 'edit' && <span className="field__hint">Web service names are fixed after creation.</span>}
        </label>

        <label className="field">
          <span className="field__label">Resource group</span>
          <select className="field__control" disabled={mode === 'edit'} value={resourceGroupId} onChange={(event) => {
            setResourceGroupId(event.target.value)
            setIsolationScopeId('')
            setTargets(undefined)
          }}>
            <option value="">Select a group</option>
            {resourceGroups.map((group) => <option key={group.id} value={group.id}>{group.name}</option>)}
          </select>
        </label>

        {mode === 'create' && (
          <div className="field">
            <label className="field__label" htmlFor="new-web-service-isolation-scope">Isolation scope</label>
            <select id="new-web-service-isolation-scope" className="field__control" value={isolationScopeId}
              disabled={!resourceGroupId || scopes.status === 'loading' || !onLoadIsolationScopes} onChange={(event) => {
              setIsolationScopeId(event.target.value)
              setTargets(undefined)
            }}>
              <option value="">{scopes.status === 'loading' ? 'Loading isolation scopes…' : 'Group default'}</option>
              {scopes.items.filter((scope) => !scope.isDefault).map((scope) => <option key={scope.id} value={scope.id}>{scope.name}</option>)}
              {unavailableScope && <option value={isolationScopeId} disabled>Selected scope unavailable</option>}
            </select>
            <span className="field__hint">Use the group's default isolation, or choose another scope by name.</span>
            {scopes.status === 'error' && <Notice tone="error">{scopes.error} You can use the group default.
              <Button onClick={scopes.retry}>Retry scopes</Button></Notice>}
          </div>
        )}

        <div className="form-grid">
          <label className="field"><span className="field__label">Image registry</span><input className="field__control" value={imageServer} onChange={(event) => setImageServer(event.target.value)} placeholder="docker.io" /></label>
          <label className="field"><span className="field__label">Image name</span><input className="field__control" value={imageName} onChange={(event) => setImageName(event.target.value)} placeholder="library/nginx" /></label>
          <label className="field"><span className="field__label">Tag</span><input className="field__control" value={imageTag} onChange={(event) => setImageTag(event.target.value)} placeholder="latest" /></label>
        </div>

        <label className="field">
          <span className="field__label">Exposure</span>
          <select className="field__control" value={exposure} onChange={(event) => {
            setExposure(event.target.value as 'Private' | 'Public')
            setTargets(undefined)
          }}>
            <option value="Private">Private</option>
            <option value="Public">Public</option>
          </select>
          <span className="field__hint">Private services have no public ingress; resource-to-resource access requires declared connections. Public services receive generated hostnames.</span>
        </label>

        {mode === 'create' && (
          <section className="form-message" aria-label="Deployment placement">
            <strong>Deployment targets</strong>
            {targetsStatus === 'loading' && <p>Loading eligible deployment targets…</p>}
            {targetsStatus === 'error' && <p>{targetsError || 'Unable to load eligible targets.'}</p>}
            {targetsStatus === 'success' && availableTargets.length === 0 && <p>No eligible target is available. A target needs workload admission{exposure === 'Public' ? ' and a public ingress base domain' : ''}.</p>}
            {exactlyOneTarget && eligibleTargets[0] && (
              <p>{eligibleTargets[0].displayName} is selected automatically. <button className="button" type="button" onClick={() => setShowAdvancedTargets((current) => !current)}>Placement options</button></p>
            )}
            {(!exactlyOneTarget || showAdvancedTargets) && eligibleTargets.map((target) => {
              const selected = targets.find((item) => item.deploymentTargetId === target.id)
              const profiles = computeProfilesByTarget[target.id] ?? []
              return (
                <div className="form-grid" key={target.id}>
                  <label className="field">
                    <span className="field__label"><input type="checkbox" checked={Boolean(selected)} onChange={(event) => setTargetSelected(target.id, event.target.checked)} /> {target.displayName}</span>
                    {exposure === 'Public' && <span className="field__hint">Generated hostname: ….{target.publicBaseDomain ?? 'no base domain'}</span>}
                  </label>
                  {selected && <>
                    <label className="field"><span className="field__label">Fixed replicas</span><input className="field__control" type="number" min="1" max="1000" value={selected.fixedReplicas} onChange={(event) => updateTarget(target.id, { fixedReplicas: Number(event.target.value) })} /></label>
                    <label className="field"><span className="field__label">Compute profile</span><select className="field__control" value={selected.computeProfileId ?? ''} onChange={(event) => updateTarget(target.id, { computeProfileId: event.target.value || undefined })}><option value="">Target default</option>{profiles.map((profile) => <option key={profile.id} value={profile.id}>{profile.displayName}{profile.isDefault ? ' (default)' : ''}</option>)}</select></label>
                  </>}
                </div>
              )
            })}
            {initialValue?.spec.autoscaling && <p>Autoscaling is shared. Its maximum capacity is charged for each selected target; fixed replicas remain the initial and fallback value.</p>}
          </section>
        )}

        <div className="form-grid form-grid--three">
          <label className="field"><span className="field__label">Probe path</span><input className="field__control" value={probePath} onChange={(event) => setProbePath(event.target.value)} /></label>
          <label className="field"><span className="field__label">Probe port</span><input className="field__control" value={probePort} onChange={(event) => setProbePort(event.target.value)} /></label>
        </div>
        <div className="form-grid form-grid--three">
          <label className="field"><span className="field__label">Service port</span><input className="field__control" type="number" min="1" max="65535" value={servicePort} onChange={(event) => setServicePort(event.target.value)} /></label>
          <label className="field"><span className="field__label">Target port</span><input className="field__control" value={targetPort} onChange={(event) => setTargetPort(event.target.value)} /></label>
          <label className="field"><span className="field__label">Port name</span><input className="field__control" value={portName} onChange={(event) => setPortName(event.target.value)} /></label>
        </div>

        <label className="field"><span className="field__label">Environment variables</span><textarea className="field__control field__control--textarea" value={environmentText} onChange={(event) => setEnvironmentText(event.target.value)} placeholder={'ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information'} /><span className="field__hint">One `KEY=value` per line.</span></label>

        {(validationError || submitError) && <Notice tone="error">{validationError || submitError}</Notice>}
        {mode === 'edit' && <div className="form-message">Shared runtime changes apply to every active deployment. Manage placement in the Deployments tab.</div>}

        <div className="resource-group-form__actions">
          <Button variant="primary" disabled={submitStatus === 'loading' || (mode === 'create' && (targetsStatus !== 'success' || scopes.status === 'loading' || unavailableScope))} type="submit">{submitStatus === 'loading' ? 'Saving…' : mode === 'create' ? 'Create service' : 'Save changes'}</Button>
          {mode === 'edit' && onCancelEdit && <button className="button" disabled={!dirty} onClick={onCancelEdit} type="button">Discard changes</button>}
        </div>
        </fieldset>
      </form>
    </section>
  )
}
