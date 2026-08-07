import { apiRequest } from './client'

export type DirectoryUser = {
  id: string
  username: string
  displayName: string
  email: string
  role: string
  siteId: string
  siteName: string
  departmentId: string
  departmentName: string
}

export type DirectoryDepartment = { id: string; code: string; name: string }
export type DirectorySite = { id: string; code: string; name: string }

export const directoryApi = {
  listUsers: () => apiRequest<DirectoryUser[]>('/api/directory/users'),
  listDepartments: () => apiRequest<DirectoryDepartment[]>('/api/directory/departments'),
  listSites: () => apiRequest<DirectorySite[]>('/api/directory/sites'),
}
