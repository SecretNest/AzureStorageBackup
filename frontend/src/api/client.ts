// 后端 API 基础客户端。所有请求走 /api（开发经 Vite proxy，生产经 nginx 反代）。

const BASE = '/api'

// 会话过期时由 App 重新挂上登录页（设计 §6）。
let onUnauthorized: (() => void) | null = null

export function setUnauthorizedHandler(handler: () => void) {
  onUnauthorized = handler
}

export class ApiError extends Error {
  status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
    this.name = 'ApiError'
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    headers: { 'Content-Type': 'application/json' },
    // fetch 默认 same-origin，会话 cookie 在跨域部署（SPA 单独托管）下根本不会被带上，
    // 后端为此开的 AllowCredentials() 就白开了。include 是 same-origin 的超集，同源部署不受影响。
    credentials: 'include',
    ...init,
  })

  if (!res.ok) {
    if (res.status === 401) onUnauthorized?.()
    const text = await res.text().catch(() => '')
    throw new ApiError(res.status, text || res.statusText)
  }

  // 204 无内容
  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}

export const api = {
  get: <T>(path: string, init?: RequestInit) => request<T>(path, init),
  post: <T>(path: string, body: unknown) =>
    request<T>(path, { method: 'POST', body: JSON.stringify(body) }),
  put: <T>(path: string, body: unknown) =>
    request<T>(path, { method: 'PUT', body: JSON.stringify(body) }),
  del: (path: string) => request<void>(path, { method: 'DELETE' }),
}
