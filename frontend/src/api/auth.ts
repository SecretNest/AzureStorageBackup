import { api } from './client'

export interface AuthStatus {
  required: boolean
  authenticated: boolean
}

export const authApi = {
  status: () => api.get<AuthStatus>('/auth/status'),
  login: (password: string) => api.post<void>('/auth/login', { password }),
  logout: () => api.post<void>('/auth/logout', {}),
}
