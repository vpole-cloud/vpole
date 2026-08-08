import { useState } from 'react'
import type { ResourceGroup, WebService, WebServiceInput } from '../types'

interface WebServiceFormProps {
  mode: 'create' | 'edit'
  resourceGroups: ResourceGroup[]
  initialValue?: WebService | null
  submitStatus: 'idle' | 'loading' | 'success' | 'error'
  submitError?: string
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

      return {
        key: key?.trim() ?? '',
        value: rest.join('=').trim(),
      }
    })
    .filter((item) => item.key)

export function WebServiceForm({
  mode,
  resourceGroups,
  initialValue,
  submitStatus,
  submitError,
  onSubmit,
  onCancelEdit,
}: WebServiceFormProps) {
  const [name, setName] = useState(initialValue?.name ?? '')
  const [resourceGroupId, setResourceGroupId] = useState(
    initialValue?.resourceGroupId ?? resourceGroups[0]?.id ?? '',
  )
  const [domainName, setDomainName] = useState(
    initialValue?.dnsRecordShortInfo?.domain ?? '',
  )
  const [imageServer, setImageServer] = useState(initialValue?.spec.image.server ?? '')
  const [imageName, setImageName] = useState(initialValue?.spec.image.name ?? '')
  const [imageTag, setImageTag] = useState(initialValue?.spec.image.tag ?? 'latest')
  const [replicas, setReplicas] = useState(String(initialValue?.spec.replicas ?? 1))
  const [probePath, setProbePath] = useState(
    initialValue?.spec.probes?.readiness.path ?? '/health',
  )
  const [probePort, setProbePort] = useState(
    initialValue?.spec.probes?.readiness.port ?? '8080',
  )
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
  const [validationError, setValidationError] = useState('')

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

    if (!domainName.trim()) {
      setValidationError('Domain is required.')
      return
    }

    setValidationError('')

    await onSubmit({
      name: name.trim(),
      resourceGroupId,
      domainName: domainName.trim(),
      imageServer: imageServer.trim(),
      imageName: imageName.trim(),
      imageTag: imageTag.trim(),
      replicas: Number(replicas),
      probePath: probePath.trim(),
      probePort: probePort.trim(),
      servicePort: Number(servicePort),
      targetPort: targetPort.trim(),
      portName: portName.trim(),
      environmentVariables: parseEnvironmentVariables(environmentText),
    })
  }

  return (
    <section className="panel web-service-form-panel">
      <div className="panel__header">
        <div>
          <p className="panel__eyebrow">
            {mode === 'create' ? 'Create' : 'Edit'}
          </p>
          <h2 className="panel__title">
            {mode === 'create' ? 'New web service' : 'Edit web service'}
          </h2>
        </div>
      </div>

      <form className="resource-group-form" onSubmit={(event) => void handleSubmit(event)}>
        <label className="field">
          <span className="field__label">Name</span>
          <input
            className="field__control"
            disabled={mode === 'edit'}
            value={name}
            onChange={(event) => setName(event.target.value)}
            placeholder="pole-api"
          />
          {mode === 'edit' && (
            <span className="field__hint">Web service names are fixed after creation.</span>
          )}
        </label>

        <label className="field">
          <span className="field__label">Resource group</span>
          <select
            className="field__control"
            disabled={mode === 'edit'}
            value={resourceGroupId}
            onChange={(event) => setResourceGroupId(event.target.value)}
          >
            <option value="">Select a group</option>
            {resourceGroups.map((group) => (
              <option key={group.id} value={group.id}>
                {group.name}
              </option>
            ))}
          </select>
        </label>

        <label className="field">
          <span className="field__label">Domain</span>
          <input
            className="field__control"
            disabled={mode === 'edit'}
            value={domainName}
            onChange={(event) => setDomainName(event.target.value)}
            placeholder="api.home.lab"
          />
        </label>

        <div className="form-grid">
          <label className="field">
            <span className="field__label">Image registry</span>
            <input
              className="field__control"
              value={imageServer}
              onChange={(event) => setImageServer(event.target.value)}
              placeholder="docker.io"
            />
          </label>
          <label className="field">
            <span className="field__label">Image name</span>
            <input
              className="field__control"
              value={imageName}
              onChange={(event) => setImageName(event.target.value)}
              placeholder="library/nginx"
            />
          </label>
          <label className="field">
            <span className="field__label">Tag</span>
            <input
              className="field__control"
              value={imageTag}
              onChange={(event) => setImageTag(event.target.value)}
              placeholder="latest"
            />
          </label>
        </div>

        <div className="form-grid">
          <label className="field">
            <span className="field__label">Replicas</span>
            <input className="field__control" type="number" min="1" value={replicas} onChange={(event) => setReplicas(event.target.value)} />
          </label>
        </div>

        <div className="form-grid form-grid--three">
          <label className="field">
            <span className="field__label">Probe path</span>
            <input className="field__control" value={probePath} onChange={(event) => setProbePath(event.target.value)} />
          </label>
          <label className="field">
            <span className="field__label">Probe port</span>
            <input className="field__control" value={probePort} onChange={(event) => setProbePort(event.target.value)} />
          </label>
        </div>

        <div className="form-grid form-grid--three">
          <label className="field">
            <span className="field__label">Service port</span>
            <input className="field__control" type="number" min="1" max="65535" value={servicePort} onChange={(event) => setServicePort(event.target.value)} />
          </label>
          <label className="field">
            <span className="field__label">Target port</span>
            <input className="field__control" value={targetPort} onChange={(event) => setTargetPort(event.target.value)} />
          </label>
          <label className="field">
            <span className="field__label">Port name</span>
            <input className="field__control" value={portName} onChange={(event) => setPortName(event.target.value)} />
          </label>
        </div>

        <label className="field">
          <span className="field__label">Environment variables</span>
          <textarea
            className="field__control field__control--textarea"
            value={environmentText}
            onChange={(event) => setEnvironmentText(event.target.value)}
            placeholder={'ASPNETCORE_ENVIRONMENT=Production\nLOG_LEVEL=Information'}
          />
          <span className="field__hint">One `KEY=value` per line.</span>
        </label>

        {(validationError || submitError) && (
          <div className="form-message form-message--error">
            {validationError || submitError}
          </div>
        )}

        {mode === 'edit' && (
          <div className="form-message">
            Name, resource group, and domain stay fixed after creation. Runtime settings can still be updated.
          </div>
        )}

        <div className="resource-group-form__actions">
          <button className="button button--primary" disabled={submitStatus === 'loading'} type="submit">
            {submitStatus === 'loading'
              ? 'Saving...'
              : mode === 'create'
                ? 'Create service'
                : 'Save changes'}
          </button>
          {mode === 'edit' && onCancelEdit && (
            <button className="button" onClick={onCancelEdit} type="button">
              Cancel
            </button>
          )}
        </div>
      </form>
    </section>
  )
}
