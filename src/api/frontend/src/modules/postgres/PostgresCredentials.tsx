import { useEffect, useState } from 'react'
import { fetchPostgresConnectionString, rotatePostgresPassword, validationMessage } from '../../api'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useApiScope } from '../../data/useApiScope'
import { useAppServices } from '../../shell/AppContext'
import { useUnsavedChanges } from '../../shell/useUnsavedChanges'
import type { PostgresCluster } from '../../types'
import { Button, ConfirmationDialog, Field, Notice } from '../../ui/components'

// Mounted only in the active credential section with write access. Values never leave memory.
export function PostgresCredentials({
  cluster,
  onDirtyChange,
  onChanged,
}: {
  cluster: PostgresCluster
  onDirtyChange: (dirty: boolean) => void
  onChanged: () => Promise<void>
}) {
  const { api, notifications } = useAppServices()
  const origin = useApiScope(api)
  const scopeKey = `${origin.identity}:${cluster.id}`
  const reveal = useScopedMutation(scopeKey)
  const rotate = useScopedMutation(scopeKey)
  const [connection, setConnection] = useState('')
  const [password, setPassword] = useState('')
  const [confirming, setConfirming] = useState(false)
  const [copyError, setCopyError] = useState('')
  const cancelReveal = reveal.cancel
  useUnsavedChanges(Boolean(password), `${cluster.name} password`)
  useEffect(() => {
    onDirtyChange(Boolean(password))
    return () => onDirtyChange(false)
  }, [password, onDirtyChange])
  useEffect(() => {
    const hide = () => {
      cancelReveal()
      setConnection('')
      setPassword('')
      setConfirming(false)
    }
    const hidden = () => {
      if (document.visibilityState === 'hidden') hide()
    }
    window.addEventListener('blur', hide)
    document.addEventListener('visibilitychange', hidden)
    return () => {
      window.removeEventListener('blur', hide)
      document.removeEventListener('visibilitychange', hidden)
    }
  }, [cancelReveal])
  const applied =
    cluster.placement.status === 'Applied' &&
    cluster.placement.observedGeneration === cluster.placement.desiredGeneration &&
    cluster.placement.endpoints.some((endpoint) => endpoint.kind === 'PostgreSql')
  return (
    <section className="panel">
      <h2 className="panel__title">Database credentials</h2>
      <p className="field__hint">
        Revealed credentials clear when you leave this section, switch tenant or leave the browser.
        Copying places the value on your clipboard.
      </p>
      {!applied && (
        <Notice>
          Connection details become available after the current deployment generation is applied.
        </Notice>
      )}
      <div className="row-actions">
        <Button
          disabled={!applied || reveal.busy || rotate.busy}
          onClick={() => {
            setCopyError('')
            void reveal
              .run(
                (signal) =>
                  fetchPostgresConnectionString(
                    api.baseUrl,
                    cluster.id,
                    origin.credential(),
                    api.tenantId,
                    signal,
                  ),
                (value) => {
                  if (origin.isCurrent()) setConnection(value.connectionString)
                },
              )
              .catch(() => {})
          }}
        >
          {reveal.busy ? 'Loading…' : 'Reveal connection string'}
        </Button>
        {connection && (
          <>
            <Button
              onClick={() => {
                if (!navigator.clipboard) {
                  setCopyError('Clipboard access is unavailable.')
                  return
                }
                void navigator.clipboard
                  .writeText(connection)
                  .then(() => {
                    if (origin.isCurrent()) notifications.publish('Connection string copied.')
                  })
                  .catch(() => {
                    if (origin.isCurrent()) setCopyError('Unable to copy connection string.')
                  })
              }}
            >
              Copy connection string
            </Button>
            <Button onClick={() => setConnection('')}>Hide connection string</Button>
          </>
        )}
      </div>
      {connection && (
        <Field label="Connection string">
          <textarea
            className="field__control"
            readOnly
            autoComplete="off"
            value={connection}
            spellCheck={false}
          />
        </Field>
      )}
      {copyError && <Notice tone="error">{copyError}</Notice>}
      {reveal.error instanceof Error && <Notice tone="error">{reveal.error.message}</Notice>}
      {cluster.spec.deploymentMode === 'Operator' ? (
        <form
          className="database-form"
          onSubmit={(event) => {
            event.preventDefault()
            if (password && !rotate.busy && !reveal.busy) setConfirming(true)
          }}
        >
          <h3>Rotate password</h3>
          <p className="field__hint">
            CloudNativePG applies the new managed Secret. Applications using the previous password
            need updated configuration.
          </p>
          <Field label="New database password">
            <input
              className="field__control"
              type="password"
              autoComplete="new-password"
              required
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              disabled={rotate.busy}
            />
            {validationMessage(rotate.error, 'Password') && (
              <span className="field-error">{validationMessage(rotate.error, 'Password')}</span>
            )}
          </Field>
          <Button type="submit" disabled={!password || rotate.busy || reveal.busy}>
            Rotate password
          </Button>
          {rotate.error instanceof Error && <Notice tone="error">{rotate.error.message}</Notice>}
        </form>
      ) : (
        <Notice>
          Password rotation is unavailable for native StatefulSet databases. Changing only the
          Secret does not change an initialized database password.
        </Notice>
      )}
      {confirming && (
        <ConfirmationDialog
          title={`Rotate password for ${cluster.name}?`}
          confirmLabel="Rotate password"
          busy={rotate.busy}
          onCancel={() => setConfirming(false)}
          onConfirm={() => {
            if (!password) return
            setConnection('')
            void rotate
              .run(
                (signal) =>
                  rotatePostgresPassword(
                    api.baseUrl,
                    cluster.id,
                    password,
                    origin.credential(),
                    api.tenantId,
                    signal,
                  ),
                async () => {
                  setPassword('')
                  setConfirming(false)
                  await onChanged()
                  if (!origin.isCurrent()) return
                  notifications.publish(
                    'Password rotation accepted. Wait for the new deployment generation to apply.',
                  )
                },
              )
              .catch(() => {})
          }}
        >
          Connected applications may need the new credentials before they can reconnect.
        </ConfirmationDialog>
      )}
    </section>
  )
}
