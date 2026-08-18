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

/** A department as Settings edits it. `userCount` is why a delete refusal can be explained up front. */
export type AdminDepartment = {
  id: string
  code: string
  name: string
  sites: DirectorySite[]
  userCount: number
}

export type AdminSite = {
  id: string
  code: string
  name: string
  departments: DirectoryDepartment[]
  userCount: number
}

export type SaveDepartmentInput = { code: string; name: string; siteIds: string[] }
export type SaveSiteInput = { code: string; name: string; departmentIds: string[] }

export const directoryApi = {
  listUsers: () => apiRequest<DirectoryUser[]>('/api/directory/users'),
  listDepartments: () => apiRequest<DirectoryDepartment[]>('/api/directory/departments'),
  listSites: () => apiRequest<DirectorySite[]>('/api/directory/sites'),

  // Settings (AdminOnly). Separate paths from the pickers above, which stay on the assets policy.
  listAdminDepartments: () => apiRequest<AdminDepartment[]>('/api/directory/admin/departments'),
  createDepartment: (input: SaveDepartmentInput) => apiRequest<AdminDepartment>('/api/directory/admin/departments', { method: 'POST', body: JSON.stringify(input) }),
  updateDepartment: (id: string, input: SaveDepartmentInput) => apiRequest<AdminDepartment>(`/api/directory/admin/departments/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  deleteDepartment: (id: string) => apiRequest<void>(`/api/directory/admin/departments/${id}`, { method: 'DELETE' }),

  listAdminSites: () => apiRequest<AdminSite[]>('/api/directory/admin/sites'),
  createSite: (input: SaveSiteInput) => apiRequest<AdminSite>('/api/directory/admin/sites', { method: 'POST', body: JSON.stringify(input) }),
  updateSite: (id: string, input: SaveSiteInput) => apiRequest<AdminSite>(`/api/directory/admin/sites/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  deleteSite: (id: string) => apiRequest<void>(`/api/directory/admin/sites/${id}`, { method: 'DELETE' }),
}
