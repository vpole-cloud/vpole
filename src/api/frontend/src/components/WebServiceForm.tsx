import { useEffect, useState } from 'react'
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
  onSubmit: (input: WebServiceInput) => Promise<void>
  onCancelEdit?: () => void
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
  onSubmit,
  onCancelEdit,
}: WebServiceFormProps) {
  const [name, setName] = useState(initialValue?.name ?? '')
  const [resourceGroupId, setResourceGroupId] = useState(
    initialValue?.resourceGroupId ?? resourceGroups[0]?.id ?? '',
  )
  const [isolationScopeId, setIsolationScopeId] = useState(initialValue?.isolationScopeId ?? '')
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
  const defaultTargetId = mode === 'create' && availableTargets.length === 1 ? availableTargets[0].id : undefined
  const targets = selectedTargets ?? (defaultTargetId ? [deploymentConfiguration(defaultTargetId)] : [])
  const [showAdvancedTargets, setShowAdvancedTargets] = useState(false)
  const [validationError, setValidationError] = useState('')

  useEffect(() => {
    if (selectedTargets === undefined && defaultTargetId) {
      void onLoadComputeProfiles(defaultTargetId)
    }
  }, [defaultTargetId, onLoadComputeProfiles, selectedTargets])

  useEffect(() => {
    if (mode !== 'create') {
      return
    }
    const controller = new AbortController()
    void onLoadTargets(
      isolationScopeId.trim() || undefined,
      exposure === 'Public',
      controller.signal,
    )
    return () => controller.abort()
  }, [exposure, isolationScopeId, mode, onLoadTargets])

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
    if (!imageServer.trim() || !imageName.trim() || !imageTag.trim()) {
      setValidationError('Image registry, name, and tag are required.')
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
  }

  const exactlyOneTarget = availableTargets.length === 1

  return (
    <section className="panel web-service-form-panel">
      <div className="panel__header">
        <div>
          <p className="panel__eyebrow">{mode === 'create' ? 'Create' : 'Edit'}</p>
          <h2 className="panel__title">{mode === 'create' ? 'New web service' : 'Edit web service'}</h2>
        </div>
      </div>

      <form className="resource-group-form" onSubmit={(event) => void handleSubmit(event)}>
        <label className="field">
          <span className="field__label">Name</span>
          <input className="field__control" disabled={mode === 'edit'} value={name} onChange={(event) => setName(event.target.value)} placeholder="pole-api" />
          {mode === 'edit' && <span className="field__hint">Web service names are fixed after creation.</span>}
        </label>

        <label className="field">
          <span className="field__label">Resource group</span>
          <select className="field__control" disabled={mode === 'edit'} value={resourceGroupId} onChange={(event) => setResourceGroupId(event.target.value)}>
            <option value="">Select a group</option>
            {resourceGroups.map((group) => <option key={group.id} value={group.id}>{group.name}</option>)}
          </select>
        </label>

        {mode === 'create' && (
          <label className="field">
            <span className="field__label">Isolation scope (optional)</span>
            <input className="field__control" value={isolationScopeId} onChange={(event) => setIsolationScopeId(event.target.value)} placeholder="MongoDB ObjectId" />
            <span className="field__hint">Eligible targets are refreshed for this placement scope.</span>
          </label>
        )}

        <div className="form-grid">
          <label className="field"><span className="field__label">Image registry</span><input className="field__control" value={imageServer} onChange={(event) => setImageServer(event.target.value)} placeholder="docker.io" /></label>
          <label className="field"><span className="field__label">Image name</span><input className="field__control" value={imageName} onChange={(event) => setImageName(event.target.value)} placeholder="library/nginx" /></label>
          <label className="field"><span className="field__label">Tag</span><input className="field__control" value={imageTag} onChange={(event) => setImageTag(event.target.value)} placeholder="latest" /></label>
        </div>

        <label className="field">
          <span className="field__label">Exposure</span>
          <select className="field__control" value={exposure} onChange={(event) => setExposure(event.target.value as 'Private' | 'Public')}>
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
            {exactlyOneTarget && availableTargets[0] && (
              <p>{availableTargets[0].displayName} is selected automatically. <button className="button" type="button" onClick={() => setShowAdvancedTargets((current) => !current)}>Placement options</button></p>
            )}
            {(!exactlyOneTarget || showAdvancedTargets) && availableTargets.map((target) => {
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

        {(validationError || submitError) && <div className="form-message form-message--error">{validationError || submitError}</div>}
        {mode === 'edit' && <div className="form-message">Placement is managed per target below. Shared runtime changes fan out atomically to every active deployment.</div>}

        <div className="resource-group-form__actions">
          <button className="button button--primary" disabled={submitStatus === 'loading'} type="submit">{submitStatus === 'loading' ? 'Saving…' : mode === 'create' ? 'Create service' : 'Save changes'}</button>
          {mode === 'edit' && onCancelEdit && <button className="button" onClick={onCancelEdit} type="button">Cancel</button>}
        </div>
      </form>
    </section>
  )
}
