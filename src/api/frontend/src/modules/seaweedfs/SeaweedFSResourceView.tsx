import { useEffect, useState } from 'react'
import {
  createSeaweedFSBucket,
  getSeaweedFS,
  listSeaweedFSBuckets,
  revealSeaweedFSCredential,
  rotateSeaweedFSBucketKey,
  updateSeaweedFSLifecycle,
  updateSeaweedFSTags,
} from '../../api'
import { useProviderInvalidation } from '../../data/useProviderInvalidation'
import { useScopedQuery } from '../../data/useScopedQuery'
import { useScopedMutation } from '../../hooks/useScopedMutation'
import { useAppServices } from '../../shell/AppContext'
import { Button, ConfirmationDialog, Field, Notice } from '../../ui/components'
import { PlacementSummary } from '../../ui/PlacementSummary'
import { ResourceHealth } from '../../ui/ResourceHealth'
import { ResourceEndpoints } from '../../ui/ResourceEndpoints'
import type { WorkspaceActions } from '../../ui/ResourceWorkspace'
import type { SeaweedFSSection } from './SeaweedFSView'
import { seaweedFSType, type SeaweedFSBucket } from './types'

type PendingAction =
  | { operation: 'delete' | 'restore'; name: string; bucketId: string | null; revision: number }
  | { operation: 'rotate'; name: string; bucketId: string; revision: number }

function Credentials({ id, bucketId }: { id: string; bucketId: string | null }) {
  const { api, tenant } = useAppServices()
  const origin = useProviderInvalidation(api)
  const mutation = useScopedMutation(`${origin.identity}:${tenant.active?.role}:${id}:${bucketId}`)
  const [values, setValues] = useState<{ accessKey: string; secretKey: string } | null>(null)
  const cancel = mutation.cancel
  useEffect(() => {
    const clear = () => {
      cancel()
      setValues(null)
    }
    window.addEventListener('blur', clear)
    document.addEventListener('visibilitychange', clear)
    return () => {
      window.removeEventListener('blur', clear)
      document.removeEventListener('visibilitychange', clear)
    }
  }, [cancel])
  return (
    <div>
      {values ? (
        <>
          <dl>
            <dt>Access key</dt>
            <dd>
              <code>{values.accessKey}</code>
            </dd>
            <dt>Secret key</dt>
            <dd>
              <code>{values.secretKey}</code>
            </dd>
          </dl>
          <Button onClick={() => setValues(null)}>Hide credentials</Button>
        </>
      ) : (
        <Button
          disabled={mutation.busy}
          onClick={() => {
            void mutation
              .run(
                (signal) =>
                  revealSeaweedFSCredential(
                    { ...api, token: origin.credential() },
                    id,
                    bucketId,
                    signal,
                  ),
                setValues,
              )
              .catch(() => {})
          }}
        >
          {bucketId ? 'Show bucket credentials' : 'Show administrator credentials'}
        </Button>
      )}
      {Boolean(mutation.error) && <Notice tone="error">Unable to retrieve credentials.</Notice>}
    </div>
  )
}

type InstanceProps = {
  id: string
  active: boolean
  actions: WorkspaceActions<SeaweedFSSection>
}

export function SeaweedFSResourceView(props: InstanceProps) {
  const { api, tenant } = useAppServices()
  const origin = useProviderInvalidation(api)
  return (
    <InstanceDetails
      key={JSON.stringify([origin.identity, tenant.active?.role, props.id])}
      {...props}
    />
  )
}

function InstanceDetails({ id, active, actions }: InstanceProps) {
  const { api, tenant, navigation } = useAppServices()
  const origin = useProviderInvalidation(api)
  const scope = [api.baseUrl, api.tenantId]
  const mutation = useScopedMutation(`${origin.identity}:${id}:${tenant.active?.role}:${active}`)
  const [name, setName] = useState('')
  const [tags, setTags] = useState<{ revision: number; value: string } | null>(null)
  const [pending, setPending] = useState<PendingAction | null>(null)
  const context = () => ({ ...api, token: origin.credential() })
  const detail = useScopedQuery({
    scope,
    key: ['seaweedfs', 'detail', id],
    active,
    interval: 15000,
    load: (signal) => getSeaweedFS(context(), id, signal),
  })
  const buckets = useScopedQuery({
    scope,
    key: ['seaweedfs', 'buckets', id],
    active,
    interval: 15000,
    load: (signal) => listSeaweedFSBuckets(context(), id, signal),
  })
  const instance = detail.resource
  const loaded = actions.loaded
  useEffect(() => {
    if (instance) loaded(id, instance.name)
  }, [id, instance, loaded])
  if (!instance)
    return detail.error ? (
      <Notice tone="error">Unable to load SeaweedFS.</Notice>
    ) : (
      <p>Loading instance…</p>
    )
  const available = instance.lifecycle === 'Active'
  const canManage = tenant.canManage && detail.isCurrent && active
  const admin = tenant.active?.role === 'Owner'
  const refresh = () =>
    origin.invalidate([['seaweedfs'], ['resource-groups'], ['search'], ['overview'], ['secrets']])
  const run = (action: (signal: AbortSignal) => Promise<unknown>, accepted: () => void) => {
    void mutation
      .run(action, () => {
        accepted()
        void refresh()
      })
      .catch(() => {})
  }
  function lifecycleAction(
    item: { id: string; name: string; revision: number; lifecycle: string },
    isBucket: boolean,
  ) {
    if (!canManage || (isBucket && (!available || !buckets.isCurrent))) return null
    const operation =
      item.lifecycle === 'Active' ? 'delete' : item.lifecycle === 'Deleting' ? 'restore' : null
    if (!operation) return null
    return (
      <Button
        disabled={mutation.busy}
        onClick={() =>
          setPending({
            operation,
            name: item.name,
            revision: item.revision,
            bucketId: isBucket ? item.id : null,
          })
        }
      >
        {operation === 'delete' ? 'Delete' : 'Restore'} {isBucket ? 'bucket' : 'instance'}
      </Button>
    )
  }
  const credential = (bucket: SeaweedFSBucket | null) =>
    active &&
    available &&
    canManage &&
    (bucket ? bucket.lifecycle === 'Active' && buckets.isCurrent : admin) && (
      <Credentials
        key={JSON.stringify([
          origin.identity,
          tenant.active?.role,
          id,
          bucket?.id,
          bucket?.revision,
          instance.revision,
        ])}
        id={id}
        bucketId={bucket?.id ?? null}
      />
    )
  return (
    <section className="panel">
      <header className="ui-page-header">
        <h1>{instance.name}</h1>
        <Button
          disabled={mutation.busy}
          onClick={() => {
            void detail.refresh()
            void buckets.refresh()
          }}
        >
          Refresh SeaweedFS
        </Button>
        {lifecycleAction(instance, false)}
      </header>
      <p>
        {instance.spec.mode === 'Replicated' ? 'Replicated on three nodes' : 'Single node'} ·{' '}
        {instance.spec.storage.sizeGb} GiB per volume server · {instance.lifecycle}
      </p>
      {instance.spec.mode === 'Replicated' && (
        <p>
          Filer metadata uses{' '}
          {instance.spec.existingPostgresId ? (
            <Button
              onClick={() =>
                navigation.openResource({
                  resourceTypeId: 'pole/postgresql',
                  resourceId: instance.spec.existingPostgresId!,
                })
              }
            >
              existing PostgreSQL cluster
            </Button>
          ) : (
            'a dedicated CNPG cluster'
          )}
          .
        </p>
      )}
      {available && canManage ? (
        <form
          onSubmit={(event) => {
            event.preventDefault()
            if (!tags) return
            run(
              (signal) =>
                updateSeaweedFSTags(
                  context(),
                  id,
                  tags.revision,
                  [
                    ...new Set(
                      tags.value
                        .split(',')
                        .map((tag) => tag.trim())
                        .filter(Boolean),
                    ),
                  ],
                  signal,
                ),
              () => setTags(null),
            )
          }}
        >
          <Field label="Tags">
            <input
              className="field__control"
              value={tags?.value ?? instance.tags.join(', ')}
              onChange={(event) =>
                setTags({
                  revision: tags?.revision ?? instance.revision,
                  value: event.target.value,
                })
              }
            />
          </Field>
          <Button
            type="submit"
            disabled={mutation.busy || tags === null || tags.revision !== instance.revision}
          >
            Save tags
          </Button>
          {tags && <Button onClick={() => setTags(null)}>Discard tag changes</Button>}
        </form>
      ) : (
        instance.tags.length > 0 && <p>Tags · {instance.tags.join(', ')}</p>
      )}
      {instance.deletionDeadlineUtc && (
        <p>
          Scheduled for permanent deletion at{' '}
          {new Date(instance.deletionDeadlineUtc).toLocaleString()}.
        </p>
      )}
      {detail.error && <Notice tone="error">Could not refresh the instance.</Notice>}
      {instance.placement && (
        <>
          <PlacementSummary placement={instance.placement} />
          {available && (
            <ResourceHealth
              placements={[instance.placement]}
              active={active}
              resourceType={seaweedFSType}
            />
          )}
        </>
      )}
      {available && (
        <>
          <ResourceEndpoints resourceType={seaweedFSType} resourceId={id} active={active} />
          <p>
            With a Private Access grant, open a local tunnel using{' '}
            <code>pole forward seaweedfs/{instance.name}</code>.
          </p>
          {credential(null)}
        </>
      )}
      <h2>Buckets</h2>
      {available && canManage && (
        <form
          onSubmit={(event) => {
            event.preventDefault()
            run(
              (signal) => createSeaweedFSBucket(context(), id, name.trim(), signal),
              () => setName(''),
            )
          }}
        >
          <Field label="Bucket name">
            <input
              className="field__control"
              value={name}
              onChange={(event) => setName(event.target.value)}
              required
              minLength={3}
              maxLength={63}
            />
          </Field>
          <Button type="submit" disabled={mutation.busy}>
            Create bucket
          </Button>
        </form>
      )}
      {buckets.error && <Notice tone="error">Could not load the bucket list.</Notice>}
      {buckets.status === 'success' && buckets.resource?.length === 0 && <p>No buckets yet.</p>}
      {buckets.resource?.map((bucket) => (
        <section key={bucket.id} className="panel">
          <h3>{bucket.name}</h3>
          <p>{bucket.lifecycle}</p>
          {bucket.deletionDeadlineUtc && (
            <p>
              Scheduled for permanent deletion at{' '}
              {new Date(bucket.deletionDeadlineUtc).toLocaleString()}.
            </p>
          )}
          {credential(bucket)}
          <div className="row-actions">
            {available && bucket.lifecycle === 'Active' && (
              <>
                <Button
                  onClick={() =>
                    navigation.openResource({
                      resourceTypeId: 'pole/secret',
                      resourceId: bucket.secretId,
                    })
                  }
                >
                  Open workload Secret
                </Button>
                {canManage && (
                  <Button
                    disabled={mutation.busy || !buckets.isCurrent}
                    onClick={() =>
                      setPending({
                        operation: 'rotate',
                        name: bucket.name,
                        bucketId: bucket.id,
                        revision: bucket.revision,
                      })
                    }
                  >
                    Rotate key
                  </Button>
                )}
              </>
            )}
            {lifecycleAction(bucket, true)}
          </div>
        </section>
      ))}
      {Boolean(mutation.error) && (
        <Notice tone="error">The request failed. Refresh the instance before retrying.</Notice>
      )}
      {pending && canManage && (
        <ConfirmationDialog
          title={`${pending.operation === 'rotate' ? 'Rotate key for' : pending.operation === 'restore' ? 'Restore' : 'Delete'} ${pending.name}?`}
          confirmLabel={
            pending.operation === 'rotate'
              ? 'Rotate key'
              : pending.operation === 'delete'
                ? 'Delete'
                : 'Restore'
          }
          busy={mutation.busy}
          onCancel={() => setPending(null)}
          onConfirm={() =>
            run(
              (signal) =>
                pending.operation === 'rotate'
                  ? rotateSeaweedFSBucketKey(
                      context(),
                      id,
                      pending.bucketId,
                      pending.revision,
                      signal,
                    )
                  : updateSeaweedFSLifecycle(
                      context(),
                      id,
                      pending.bucketId,
                      pending.operation,
                      pending.revision,
                      signal,
                    ),
              () => setPending(null),
            )
          }
        >
          {pending.operation === 'delete'
            ? 'Access will close. Data is retained until the scheduled deletion time.'
            : pending.operation === 'rotate'
              ? 'Clients using the old key will need the new credential.'
              : 'The resource will become available after reconciliation.'}
        </ConfirmationDialog>
      )}
    </section>
  )
}
