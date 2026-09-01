// The base API client. Every request goes through /api (via the Vite proxy in development, via the
// reverse proxy in production).
//
// App remounts the login page when the session expires (design §6).
const BASE = '/api'
let onUnauthorized: (() => void) | null = null

export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler
}

export class ApiError extends Error {
  status: number
  /** A machine-readable code the backend attaches in some cases, e.g. keyring_lost. */
  code?: string

  constructor(status: number, message: string, code?: string) {
    super(message)
    this.status = status
    this.code = code
    this.name = 'ApiError'
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    // fetch defaults to same-origin, so the session cookie is not sent at all under a cross-origin
    // deployment (the SPA hosted separately), which would make the backend's AllowCredentials()
    // pointless. include is a superset of same-origin, so same-origin deployments are unaffected.
    credentials: 'include',
    ...init,
  })

  if (!res.ok) {
    if (res.status === 401) onUnauthorized?.()
    const text = await res.text().catch(() => '')

    // The backend reports errors uniformly as { error, code? }. Without parsing it, the user sees
    // the raw JSON — or, when the body is empty, a fallback to something as uninformative as
    // "Internal Server Error".
    let message = text || res.statusText
    let code: string | undefined
    try {
      const body = JSON.parse(text) as { error?: unknown; code?: unknown }
      if (typeof body.error === 'string' && body.error) message = body.error
      if (typeof body.code === 'string') code = body.code
    } catch {
      // Not JSON (an HTML error page from a reverse proxy, say): keep the raw text.
    }

    throw new ApiError(res.status, message, code)
  }

  // 204, no content — and any other success with an empty body (202 Accepted without a payload, say):
  // res.json() on an empty body throws "Unexpected end of JSON input", which surfaced as a red banner the
  // instant a suspend was accepted. An empty success simply has nothing to say.
  if (res.status === 204) return undefined as T
  const body = await res.text()
  return (body ? JSON.parse(body) : undefined) as T
}

export const api = {
  get: <T>(path: string, init?: RequestInit) => request<T>(path, init),
  // init, like get: writes the user is waiting on want a signal, so a request that never gets through
  // ends in a message rather than a button that stays greyed out for the rest of the session.
  post: <T>(path: string, body: unknown, init?: RequestInit) =>
    request<T>(path, { ...init, method: 'POST', body: JSON.stringify(body) }),
  put: <T>(path: string, body: unknown, init?: RequestInit) =>
    request<T>(path, { ...init, method: 'PUT', body: JSON.stringify(body) }),
  del: (path: string) => request<void>(path, { method: 'DELETE' }),
}
