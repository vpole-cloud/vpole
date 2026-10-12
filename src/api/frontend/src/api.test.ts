import { describe, expect, it, vi } from 'vitest'
import {
  addWebServiceHostnameBinding,
  createWebService,
  deleteWebServiceHostnameBinding,
  fetchAvailableDeploymentTargets,
  fetchWebServiceHostnameBindings,
  normalizeTenantRole,
  fetchResourceGroups,
  fetchResourceGroup,
  ApiError,
  validationMessage,
  retryWebServiceDeployment,
  searchResources,
  fetchProviderRepositories,
  fetchProviderBranches,
  createSecret,
  fetchSecret,
  SecretApiError,
} from './api'
import { retryOperationForStatus } from './components/webServiceDeploymentStatus'

describe('shared inventory API', () => {
  it.each([
    JSON.stringify({ code: 'secrets.version-conflict', detail: 'private-canary', errors: { 'Body.Values': ['private-canary'] } }),
    JSON.stringify({ code: 'private-canary', title: 'private-canary' }),
    'private-canary',
  ])('never surfaces Secret values or arbitrary problem details: case %#', async (body) => {
    const fetchMock = vi.fn().mockResolvedValue(new Response(body, { status: 409 }))
    vi.stubGlobal('fetch', fetchMock)
    const failure = await createSecret('https://pole.test', { name: 'app', purpose: '', resourceGroupId: 'group', isolationScopeId: 'scope', values: { token: 'private-canary' } }, 'bearer', 'tenant').catch((error: unknown) => error)
    expect(failure).toBeInstanceOf(SecretApiError)
    expect(JSON.stringify(failure)).not.toContain('private-canary')
    expect((failure as Error).message).not.toContain('private-canary')
    expect((failure as SecretApiError).fieldErrors).toEqual({})
    expect(fetchMock.mock.calls[0][1]).toMatchObject({ cache: 'no-store', headers: { Authorization: 'Bearer bearer', 'X-Pole-Tenant-ID': 'tenant' } })
    vi.unstubAllGlobals()
  })
  it('redacts Secret read and network errors as well as write errors', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('private-canary')))
    const failure = await fetchSecret('https://pole.test', 'id').catch((error: unknown) => error)
    expect(failure).toBeInstanceOf(SecretApiError)
    expect((failure as Error).message).not.toContain('private-canary')
    vi.unstubAllGlobals()
  })
  it('scopes search and paged Git discovery with bearer headers and cancellation', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => ({ items: [] }) })
    vi.stubGlobal('fetch', fetchMock)
    const signal = new AbortController().signal
    await searchResources('https://pole.test', ' app & worker ', 'token', 'tenant', signal, 2)
    await fetchProviderRepositories('https://pole.test', 'account/id', 'token', 'tenant', signal, 3)
    await fetchProviderBranches('https://pole.test', 'account', 'repository/id', 'token', 'tenant', signal, 2)
    expect(fetchMock.mock.calls.map((call) => call[0])).toEqual([
      'https://pole.test/api/v1/search?query=app+%26+worker&pageNumber=2&pageSize=20',
      'https://pole.test/api/v1/git-provider-connections/account%2Fid/repositories?page=3&pageSize=100',
      'https://pole.test/api/v1/git-provider-connections/account/repositories/repository%2Fid/branches?page=2&pageSize=100',
    ])
    for (const call of fetchMock.mock.calls) expect(call[1]).toEqual(expect.objectContaining({ signal, headers: expect.objectContaining({ Authorization: 'Bearer token', 'X-Pole-Tenant-ID': 'tenant' }) }))
  })
  it('sends requested group pages and encoded direct identities with scoped headers and cancellation', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => ({ items: [] }) })
    vi.stubGlobal('fetch', fetchMock)
    const signal = new AbortController().signal
    await fetchResourceGroups('https://pole.test', 'token', 'tenant', signal, { pageNumber: 3, pageSize: 20, search: ' prod ' })
    await fetchResourceGroup('https://pole.test', 'group/identity', 'token', 'tenant', signal)
    expect(fetchMock.mock.calls[0]).toEqual(['https://pole.test/api/v1/resource-groups?pageNumber=3&pageSize=20&search=prod', expect.objectContaining({ signal, headers: expect.objectContaining({ Authorization: 'Bearer token', 'X-Pole-Tenant-ID': 'tenant' }) })])
    expect(fetchMock.mock.calls[1][0]).toBe('https://pole.test/api/v1/resource-groups/group%2Fidentity')
  })
  it('parses problem details and associates nested validation messages with their fields', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ title: 'Validation failed', code: 'validation.failed', errors: { 'Body.Name': ['Name already exists.'], 'Body.Spec.Image.Name': ['Choose an image.'], 'Body.Tags[0]': ['Tag too long.'] } }), { status: 400 })))
    const failure = await fetchResourceGroup('https://pole.test', 'group').catch((error: unknown) => error)
    expect(failure).toBeInstanceOf(ApiError)
    expect((failure as ApiError).status).toBe(400)
    expect((failure as ApiError).code).toBe('validation.failed')
    expect(validationMessage(failure, 'name')).toBe('Name already exists.')
    expect(validationMessage(failure, 'spec.image')).toBe('Choose an image.')
    expect(validationMessage(failure, 'tags')).toBe('Tag too long.')
    expect(validationMessage(failure, 'description')).toBe('')
    expect((failure as Error).message).not.toContain('"errors"')
  })
  it.each(['Upstream unavailable', ''])('keeps non-problem failures readable', async (body) => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(body, { status: 503 })))
    await expect(fetchResourceGroup('https://pole.test', 'group')).rejects.toThrow(body || 'Request failed with status 503')
  })
})

describe('tenant role normalization', () => {
  it('maps the numeric API enum to the frontend role contract', () => {
    expect(normalizeTenantRole(3)).toBe('Owner')
    expect(normalizeTenantRole(1)).toBe('Member')
  })

  it('preserves a string role and rejects an unknown enum value', () => {
    expect(normalizeTenantRole('Admin')).toBe('Admin')
    expect(() => normalizeTenantRole(99)).toThrow('Unknown tenant role value: 99')
  })
})

describe('web service placement API', () => {
  it('sends tenant-scoped target discovery for public web service placement', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => [],
    })
    vi.stubGlobal('fetch', fetchMock)

    await fetchAvailableDeploymentTargets(
      'https://pole.test',
      '507f1f77bcf86cd799439011',
      'token',
      '507f1f77bcf86cd799439012',
    )

    expect(fetchMock).toHaveBeenCalledWith(
      'https://pole.test/api/v1/deployment-targets/available?resourceType=pole%2Fweb-service&requiresPublicIngress=true&isolationScopeId=507f1f77bcf86cd799439011',
      expect.objectContaining({
        headers: expect.objectContaining({
          Authorization: 'Bearer token',
          'X-Pole-Tenant-ID': '507f1f77bcf86cd799439012',
        }),
      }),
    )
  })

  it('loads private workload targets without requiring public ingress', async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: true, json: async () => [] })
    vi.stubGlobal('fetch', fetchMock)

    await fetchAvailableDeploymentTargets(
      'https://pole.test',
      undefined,
      undefined,
      undefined,
      undefined,
      false,
    )

    expect(fetchMock).toHaveBeenCalledWith(
      'https://pole.test/api/v1/deployment-targets/available?resourceType=pole%2Fweb-service&requiresPublicIngress=false',
      expect.any(Object),
    )
  })

  it('sends per-target placement and never sends the retired shared replicas or domain fields', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({ webServiceId: '507f1f77bcf86cd799439013', deployments: [] }),
    })
    vi.stubGlobal('fetch', fetchMock)

    await createWebService('https://pole.test', {
      expectedRevision: 2,
      name: 'api',
      resourceGroupId: '507f1f77bcf86cd799439014',
      exposure: 'Private',
      isolationScopeId: '507f1f77bcf86cd799439015',
      imageServer: 'docker.io',
      imageName: 'library/nginx',
      imageTag: 'latest',
      probePath: '/health',
      probePort: 'http',
      servicePort: 80,
      targetPort: '8080',
      portName: 'http',
      environmentVariables: [],
      autoscaling: null,
      targets: [{ deploymentTargetId: '507f1f77bcf86cd799439016', fixedReplicas: 2 }],
    })

    const request = fetchMock.mock.calls.at(0)?.[1] as RequestInit
    const body = JSON.parse(String(request.body))
    expect(body.placement).toEqual({
      isolationScopeId: '507f1f77bcf86cd799439015',
      targets: [{ deploymentTargetId: '507f1f77bcf86cd799439016', fixedReplicas: 2, computeProfileId: null }],
    })
    expect(body.exposure).toBe('Private')
    expect(body).not.toHaveProperty('expectedRevision')
    expect(body.spec).not.toHaveProperty('replicas')
    expect(body).not.toHaveProperty('domainName')
  })

  it('preserves a delete operation when retrying a failed deletion', async () => {
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      json: async () => ({}),
    })
    vi.stubGlobal('fetch', fetchMock)

    expect(retryOperationForStatus('DeleteFailed')).toBe('Delete')
    await retryWebServiceDeployment(
      'https://pole.test',
      '507f1f77bcf86cd799439013',
      '507f1f77bcf86cd799439016',
      { expectedDeploymentRevision: 4, expectedOperation: retryOperationForStatus('DeleteFailed') },
    )

    const request = fetchMock.mock.calls.at(0)?.[1] as RequestInit
    expect(JSON.parse(String(request.body))).toEqual({
      expectedDeploymentRevision: 4,
      expectedOperation: 'Delete',
    })
  })

  it('uses placement responses for hostname mutations and a string resource type for bindings', async () => {
    const placement = {
      deploymentId: '507f1f77bcf86cd799439016',
      runtimeCellId: '507f1f77bcf86cd799439017',
      deploymentTargetId: '507f1f77bcf86cd799439018',
      desiredGeneration: 3,
      observedGeneration: 2,
      status: 'Pending',
      failureCode: null,
      compute: {},
      endpoints: [],
      resourceRevision: 4,
      fixedReplicas: 2,
      ingress: null,
      deploymentRevision: 5,
      desiredOperation: 'Apply',
    }
    const fetchMock = vi.fn()
      .mockResolvedValueOnce({
        ok: true,
        json: async () => [{
          id: '507f1f77bcf86cd799439019',
          resourceType: 'pole/web-service',
        }],
      })
      .mockResolvedValueOnce({ ok: true, json: async () => placement })
      .mockResolvedValueOnce({ ok: true, json: async () => placement })
    vi.stubGlobal('fetch', fetchMock)

    const bindings = await fetchWebServiceHostnameBindings(
      'https://pole.test',
      '507f1f77bcf86cd799439013',
      placement.deploymentId,
    )
    expect(bindings[0]?.resourceType).toBe('pole/web-service')

    const added = await addWebServiceHostnameBinding(
      'https://pole.test',
      '507f1f77bcf86cd799439013',
      placement.deploymentId,
      {
        tenantDomainZoneId: '507f1f77bcf86cd799439020',
        relativeName: 'api',
      },
    )
    expect(added).toEqual(placement)

    const removed = await deleteWebServiceHostnameBinding(
      'https://pole.test',
      '507f1f77bcf86cd799439013',
      placement.deploymentId,
      '507f1f77bcf86cd799439019',
    )
    expect(removed).toEqual(placement)
    expect(fetchMock.mock.calls[1]?.[1]).toEqual(expect.objectContaining({
      method: 'POST',
      body: JSON.stringify({
        tenantDomainZoneId: '507f1f77bcf86cd799439020',
        relativeName: 'api',
      }),
    }))
    expect(fetchMock.mock.calls[2]?.[1]).toEqual(expect.objectContaining({
      method: 'DELETE',
    }))
  })
})
