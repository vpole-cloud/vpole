// All HTTP I/O passes through this transport; feature clients own routes and DTO mapping.
export const request = (input: RequestInfo | URL, init?: RequestInit) => fetch(input, init)

export const buildHeaders = (token?: string, tenantId?: string) => {
  const headers: Record<string, string> = {
    Accept: 'application/json',
  }

  if (token) {
    headers.Authorization = `Bearer ${token}`
  }

  if (tenantId) {
    headers['X-Pole-Tenant-ID'] = tenantId
  }

  return headers
}

export class ApiError extends Error {
  readonly status: number
  readonly code: string
  fieldErrors: Record<string, string[]>
  constructor(message: string, status: number, fieldErrors: Record<string, string[]> = {}, code = '') {
    super(message)
    this.name = 'ApiError'
    this.code = code
    this.status = status
    this.fieldErrors = fieldErrors
  }
}

export function validationMessage(error: unknown, field: string): string {
  if (!(error instanceof ApiError)) return ''
  return Object.entries(error.fieldErrors).filter(([path]) => {
    const normalized = path.replace(/^body\./i, '').replace(/\[\d+\]/g, '').toLowerCase()
    return normalized === field.toLowerCase() || normalized.startsWith(`${field.toLowerCase()}.`)
  }).flatMap(([, messages]) => messages).join(' ')
}

export async function responseError(response: Response): Promise<ApiError> {
  const text = await response.text()
  const fallback = text || `Request failed with status ${response.status}`
  try {
    const problem: unknown = JSON.parse(text)
    if (!problem || typeof problem !== 'object') return new ApiError(fallback, response.status)
    const value = problem as { detail?: unknown; title?: unknown; errors?: unknown; code?: unknown }
    const fields: Record<string, string[]> = {}
    if (value.errors && typeof value.errors === 'object') {
      for (const [key, messages] of Object.entries(value.errors)) {
        if (Array.isArray(messages)) fields[key] = messages.filter((message): message is string => typeof message === 'string')
      }
    }
    const description = typeof value.detail === 'string' ? value.detail : typeof value.title === 'string' ? value.title : ''
    const messages = Object.values(fields).flat().join(' ')
    return new ApiError([description, messages].filter(Boolean).join(' ') || fallback, response.status, fields, typeof value.code === 'string' ? value.code : '')
  } catch { return new ApiError(fallback, response.status) }
}

export async function fetchJson<T>(
  input: RequestInfo | URL,
  init?: RequestInit,
): Promise<T> {
  const response = await request(input, init)

  if (!response.ok) {
    throw await responseError(response)
  }

  return (await response.json()) as T
}

export async function sendJson<T>(
  input: RequestInfo | URL,
  init: RequestInit,
): Promise<T> {
  return fetchJson<T>(input, {
    ...init,
    headers: {
      'Content-Type': 'application/json',
      ...(init.headers ?? {}),
    },
  })
}

export async function sendWithoutResponse(input: RequestInfo | URL, init: RequestInit) {
  const response = await request(input, init)
  if (!response.ok) {
    throw await responseError(response)
  }
}
