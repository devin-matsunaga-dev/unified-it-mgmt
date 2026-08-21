import type { Ci } from './assets'
import { apiRequest } from './client'

export type ContractType = 'Support' | 'Warranty' | 'Maintenance' | 'Lease' | 'Subscription'

/** Where a due date sits relative to today. "Expiring soon" is the same 30 days the job notices on. */
export type ContractExpiryStatus = 'Active' | 'ExpiringSoon' | 'Expired'

export type Vendor = {
  id: string
  name: string
  contactName: string | null
  contactEmail: string | null
  contactPhone: string | null
  website: string | null
  notes: string | null
  isActive: boolean
  contractCount: number
  createdAt: string
  updatedAt: string
}

export type VendorPage = { items: Vendor[]; total: number; page: number; pageSize: number }

export type Contract = {
  id: string
  vendorId: string
  vendorName: string
  poNumber: string
  name: string
  type: ContractType
  startDate: string
  endDate: string
  autoRenews: boolean
  cost: number | null
  currency: string | null
  ownerUserId: string | null
  ownerName: string | null
  ownerEmail: string | null
  departmentId: string | null
  /** Snapshotted beside the id, so the record stays readable after a department is renamed. */
  departmentName: string | null
  /** The agreement's own reference, if it has one. Distinct from the PO that bought it. */
  contractNumber: string | null
  notes: string | null
  isActive: boolean
  status: ContractExpiryStatus
  daysRemaining: number
  coveredCiCount: number
  createdAt: string
  updatedAt: string
}

export type ContractPage = { items: Contract[]; total: number; page: number; pageSize: number }

export type ContractFilter = {
  search?: string
  vendorId?: string
  status?: ContractExpiryStatus
  type?: ContractType
  isActive?: boolean
  page?: number
  pageSize?: number
}

export type VendorInput = {
  name: string
  contactName: string | null
  contactEmail: string | null
  contactPhone: string | null
  website: string | null
  notes: string | null
}

export type ContractInput = {
  vendorId: string
  poNumber: string
  name: string
  type: ContractType
  startDate: string
  endDate: string
  autoRenews: boolean
  cost: number | null
  currency: string | null
  ownerUserId: string | null
  departmentId: string | null
  contractNumber: string | null
  notes: string | null
}

/** A complete statement of what covers a CI: omitting the contract releases it. */
export type CiCoverageInput = {
  contractId: string | null
  purchaseDate: string | null
  warrantyExpiresAt: string | null
}

export type ContractNotification = {
  id: string
  subject: 'Contract' | 'Warranty'
  subjectId: string
  subjectName: string
  dueDate: string
  thresholdDays: number
  recipient: string
  message: string
  sentAt: string
}

export type ContractExpiryRun = {
  runDate: string
  contractsScanned: number
  warrantiesScanned: number
  raised: ContractNotification[]
}

export const contractTypes: ContractType[] = ['Support', 'Warranty', 'Maintenance', 'Lease', 'Subscription']

const statusLabels: Record<ContractExpiryStatus, string> = {
  Active: 'Active',
  ExpiringSoon: 'Expiring soon',
  Expired: 'Expired',
}

/** Pill classes per DESIGN.md §3: amber warns, red is past due, green is fine. */
const statusTones: Record<ContractExpiryStatus, string> = {
  Active: 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-400',
  ExpiringSoon: 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-400',
  Expired: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-400',
}

export function contractStatusLabel(status: string) {
  return statusLabels[status as ContractExpiryStatus] ?? status
}

export function contractStatusTone(status: string) {
  return statusTones[status as ContractExpiryStatus] ?? statusTones.Active
}

/** "in 12 days" / "today" / "8 days ago" — the same wording the notices use. */
export function describeDaysRemaining(days: number) {
  if (days > 1) return `in ${days} days`
  if (days === 1) return 'tomorrow'
  if (days === 0) return 'today'
  if (days === -1) return 'yesterday'
  return `${Math.abs(days)} days ago`
}

export function contractFilterToQuery(filter: ContractFilter) {
  const query = new URLSearchParams()
  if (filter.search?.trim()) query.set('search', filter.search.trim())
  if (filter.vendorId) query.set('vendorId', filter.vendorId)
  if (filter.status) query.set('status', filter.status)
  if (filter.type) query.set('type', filter.type)
  if (filter.isActive !== undefined) query.set('isActive', String(filter.isActive))
  query.set('page', String(filter.page ?? 1))
  query.set('pageSize', String(filter.pageSize ?? 25))
  return query.toString()
}

export const contractsApi = {
  getReminderSettings: () =>
    apiRequest<ContractReminderSettings>('/api/contract-reminder-settings'),
  saveReminderSettings: (input: SaveContractReminderSettingsInput) =>
    apiRequest<ContractReminderSettings>('/api/contract-reminder-settings', {
      method: 'PUT', body: JSON.stringify(input),
    }),
  listVendors: (search = '') => apiRequest<VendorPage>(`/api/vendors?pageSize=200${search ? `&search=${encodeURIComponent(search)}` : ''}`),
  createVendor: (input: VendorInput) => apiRequest<Vendor>('/api/vendors', { method: 'POST', body: JSON.stringify(input) }),
  updateVendor: (id: string, input: VendorInput & { isActive: boolean }) =>
    apiRequest<Vendor>(`/api/vendors/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  deleteVendor: (id: string) => apiRequest<void>(`/api/vendors/${id}`, { method: 'DELETE' }),
  listContracts: (filter: ContractFilter = {}) => apiRequest<ContractPage>(`/api/contracts?${contractFilterToQuery(filter)}`),
  getContract: (id: string) => apiRequest<Contract>(`/api/contracts/${id}`),
  createContract: (input: ContractInput) => apiRequest<Contract>('/api/contracts', { method: 'POST', body: JSON.stringify(input) }),
  updateContract: (id: string, input: ContractInput & { isActive: boolean }) =>
    apiRequest<Contract>(`/api/contracts/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  deleteContract: (id: string) => apiRequest<void>(`/api/contracts/${id}`, { method: 'DELETE' }),
  setCoverage: (ciId: string, input: CiCoverageInput) =>
    apiRequest<Ci>(`/api/cis/${ciId}/coverage`, { method: 'PUT', body: JSON.stringify(input) }),
  listNotifications: (limit = 50) => apiRequest<ContractNotification[]>(`/api/contract-notifications?limit=${limit}`),
  runExpiryPass: () => apiRequest<ContractExpiryRun>('/api/contract-notifications/runs', { method: 'POST' }),
}

/** How far ahead of an expiry renewal notices go out. One row for the whole platform. */
export type ContractReminderSettings = {
  /** Days before expiry, widest first. Empty means notices are switched off. */
  thresholdDays: number[]
  enabled: boolean
  /** Who every contract notice goes to. Empty falls back to each contract's own owner. */
  recipients: string[]
  updatedBy: string
  updatedAt: string
}

export type SaveContractReminderSettingsInput = {
  thresholdDays: number[]
  enabled: boolean
  recipients: string[]
}
