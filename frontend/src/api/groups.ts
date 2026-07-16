import { api } from './client'

export interface GroupMember {
  accountId: number
  containerName: string
}

export interface Group {
  id: number
  name: string
  members: GroupMember[]
  createdAt: string
}

export interface GroupInput {
  name: string
  members: GroupMember[]
}

export const groupsApi = {
  list: () => api.get<Group[]>('/groups'),
  get: (id: number) => api.get<Group>(`/groups/${id}`),
  create: (g: GroupInput) => api.post<Group>('/groups', g),
  update: (id: number, g: GroupInput) => api.put<Group>(`/groups/${id}`, g),
  remove: (id: number) => api.del(`/groups/${id}`),
}
