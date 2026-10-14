import { authorizedFetch, API_BASE } from '../auth'

/** Empty by default, so requests go to the dev server's /api proxy on this same origin. */
export const BASE_URL = API_BASE

export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number | null,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

/**
 * The message to show for a failure. Minimal-API validation failures come back as `{ "error": ... }`,
 * and a request that never reached the API (the usual case — it is not running) has no status at all.
 */
export async function toError(response: Response): Promise<ApiError> {
  let message = `${response.status} ${response.statusText}`
  try {
    const body = await response.json()
    if (body && typeof body === 'object') {
      const detail = (body as Record<string, unknown>).error ?? (body as Record<string, unknown>).title
      if (typeof detail === 'string' && detail.length > 0) {
        message = detail
      }
    }
  } catch {
    // A non-JSON error body leaves the status line as the message.
  }
  return new ApiError(message, response.status)
}

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
  let response: Response
  try {
    response = await authorizedFetch(`${BASE_URL}${path}`, {
      ...init,
      headers: { Accept: 'application/json', ...init?.headers },
    })
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') {
      throw cause
    }
    throw new ApiError('Could not reach the API. Is SharePointAgent.Api running?', null)
  }

  if (!response.ok) {
    throw await toError(response)
  }
  if (response.status === 204 || response.status === 205) {
    return undefined as T
  }
  return (await response.json()) as T
}

export function query(params: Record<string, string | number | boolean | undefined | null>): string {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== '') {
      search.set(key, String(value))
    }
  }
  const text = search.toString()
  return text ? `?${text}` : ''
}

export async function downloadBlob(path: string, signal?: AbortSignal): Promise<Blob> {
  let response: Response
  try {
    response = await authorizedFetch(`${BASE_URL}${path}`, { signal })
  } catch (cause) {
    if (cause instanceof DOMException && cause.name === 'AbortError') {
      throw cause
    }
    throw new ApiError('Could not reach the API. Is SharePointAgent.Api running?', null)
  }
  if (!response.ok) {
    throw await toError(response)
  }
  return response.blob()
}
