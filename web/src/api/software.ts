import { apiRequest, apiUpload } from './client'
import type { ContractExpiryStatus } from './contracts'

export type SoftwareMatchKind = 'Exact' | 'Prefix' | 'Contains'

export type SoftwareProduct = {
  id: string
  name: string
  publisher: string
  category: string | null
  notes: string | null
  isActive: boolean
  ruleCount: number
  licensePoolCount: number
  installCount: number
  createdAt: string
  updatedAt: string
}

export type SoftwareProductPage = { items: SoftwareProduct[]; total: number; page: number; pageSize: number }

export type SoftwareProductInput = {
  name: string
  publisher: string
  category: string | null
  notes: string | null
}

export type SoftwareRule = {
  id: string
  productId: string
  productName: string
  publisher: string
  matchKind: SoftwareMatchKind
  pattern: string
  priority: number
  isActive: boolean
  createdAt: string
  updatedAt: string
}

export type SoftwareRuleInput = {
  productId: string
  matchKind: SoftwareMatchKind
  pattern: string
  priority: number
}

export type InstalledSoftware = {
  id: string
  ciId: string
  ciName: string
  rawName: string
  rawPublisher: string | null
  version: string | null
  productId: string | null
  productName: string | null
  productPublisher: string | null
  installedOn: string | null
  source: string
  firstSeenAt: string
  lastSeenAt: string
  sightingCount: number
}

export type InstalledSoftwarePage = { items: InstalledSoftware[]; total: number; page: number; pageSize: number }

export type InstalledSoftwareFilter = {
  ciId?: string
  productId?: string
  isNormalised?: boolean
  search?: string
  page?: number
  pageSize?: number
}

export type UnrecognisedSoftware = {
  rawName: string
  rawPublisher: string | null
  installCount: number
  ciCount: number
}

export type SoftwareNormalisationRun = {
  installsExamined: number
  normalised: number
  renormalised: number
  unrecognised: number
}

export type LicensePool = {
  id: string
  productId: string
  productName: string
  publisher: string
  name: string
  reference: string | null
  entitlements: number
  purchaseDate: string | null
  expiresAt: string | null
  notes: string | null
  isActive: boolean
  status: ContractExpiryStatus | null
  daysRemaining: number | null
  createdAt: string
  updatedAt: string
}

export type LicensePoolPage = { items: LicensePool[]; total: number; page: number; pageSize: number }

export type LicensePoolInput = {
  productId: string
  name: string
  reference: string | null
  entitlements: number
  purchaseDate: string | null
  expiresAt: string | null
  notes: string | null
}

export type LicensePoolFilter = {
  search?: string
  productId?: string
  status?: ContractExpiryStatus
  isActive?: boolean
  page?: number
  pageSize?: number
}

export type SoftwareComplianceState = 'Compliant' | 'OverDeployed' | 'Unlicensed' | 'Unused'

export type SoftwareComplianceRow = {
  productId: string
  productName: string
  publisher: string
  category: string | null
  installedCiCount: number
  installCount: number
  entitled: number
  licensePoolCount: number
  expiredPoolCount: number
  overage: number
  state: SoftwareComplianceState
  nextExpiry: string | null
  expiryStatus: ContractExpiryStatus | null
}

export type SoftwareCompliance = {
  generatedOn: string
  productCount: number
  overDeployedCount: number
  unlicensedCount: number
  totalInstalls: number
  totalEntitled: number
  rows: SoftwareComplianceRow[]
}

export type SoftwareComplianceRun = {
  today: string
  productsChecked: number
  overDeployed: number
  raised: { id: string; subjectName: string; message: string; thresholdDays: number }[]
}

export type SoftwareImportAction = 'Create' | 'Update' | 'Error'

export type SoftwareImportRow = {
  lineNumber: number
  action: SoftwareImportAction
  machine: string | null
  softwareName: string | null
  version: string | null
  ciId: string | null
  ciName: string | null
  productId: string | null
  productName: string | null
  errors: string[]
}

export type SoftwareImportReport = {
  isDryRun: boolean
  fileName: string
  totalRows: number
  created: number
  updated: number
  failed: number
  machinesMatched: number
  normalised: number
  unrecognised: number
  rows: SoftwareImportRow[]
  unrecognisedNames: string[]
}

export const softwareMatchKinds: SoftwareMatchKind[] = ['Exact', 'Prefix', 'Contains']

const complianceLabels: Record<SoftwareComplianceState, string> = {
  Compliant: 'Compliant',
  OverDeployed: 'Over-deployed',
  Unlicensed: 'Unlicensed',
  Unused: 'Unused',
}

/**
 * Pill classes per DESIGN.md §3. Over-deployment is the red one because it is the only state that
 * costs money if nobody acts; unlicensed is amber because a free utility lives there too.
 */
const complianceTones: Record<SoftwareComplianceState, string> = {
  Compliant: 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-400',
  OverDeployed: 'bg-red-100 text-red-700 dark:bg-red-500/15 dark:text-red-400',
  Unlicensed: 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-400',
  Unused: 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400',
}

export function complianceLabel(state: string) {
  return complianceLabels[state as SoftwareComplianceState] ?? state
}

export function complianceTone(state: string) {
  return complianceTones[state as SoftwareComplianceState] ?? complianceTones.Compliant
}

/** "3 over" / "2 spare" / "exactly covered" — the shortfall said in words rather than a signed number. */
export function describeOverage(row: SoftwareComplianceRow) {
  if (row.state === 'Unlicensed') return `${row.installedCiCount} installed, none entitled`
  if (row.overage > 0) return `${row.overage} over`
  if (row.overage === 0) return 'exactly covered'
  return `${Math.abs(row.overage)} spare`
}

function installFilterToQuery(filter: InstalledSoftwareFilter) {
  const query = new URLSearchParams()
  if (filter.ciId) query.set('ciId', filter.ciId)
  if (filter.productId) query.set('productId', filter.productId)
  if (filter.isNormalised !== undefined) query.set('isNormalised', String(filter.isNormalised))
  if (filter.search?.trim()) query.set('search', filter.search.trim())
  query.set('page', String(filter.page ?? 1))
  query.set('pageSize', String(filter.pageSize ?? 25))
  return query.toString()
}

function poolFilterToQuery(filter: LicensePoolFilter) {
  const query = new URLSearchParams()
  if (filter.search?.trim()) query.set('search', filter.search.trim())
  if (filter.productId) query.set('productId', filter.productId)
  if (filter.status) query.set('status', filter.status)
  if (filter.isActive !== undefined) query.set('isActive', String(filter.isActive))
  query.set('page', String(filter.page ?? 1))
  query.set('pageSize', String(filter.pageSize ?? 25))
  return query.toString()
}

function formData(file: File) {
  const body = new FormData()
  body.append('file', file)
  return body
}

export const softwareApi = {
  listProducts: (search = '') =>
    apiRequest<SoftwareProductPage>(`/api/software-products?pageSize=200${search ? `&search=${encodeURIComponent(search)}` : ''}`),
  createProduct: (input: SoftwareProductInput) =>
    apiRequest<SoftwareProduct>('/api/software-products', { method: 'POST', body: JSON.stringify(input) }),
  updateProduct: (id: string, input: SoftwareProductInput & { isActive: boolean }) =>
    apiRequest<SoftwareProduct>(`/api/software-products/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  deleteProduct: (id: string) => apiRequest<void>(`/api/software-products/${id}`, { method: 'DELETE' }),

  listRules: (productId?: string) =>
    apiRequest<SoftwareRule[]>(`/api/software-normalisation-rules${productId ? `?productId=${productId}` : ''}`),
  createRule: (input: SoftwareRuleInput) =>
    apiRequest<SoftwareRule>('/api/software-normalisation-rules', { method: 'POST', body: JSON.stringify(input) }),
  deleteRule: (id: string) => apiRequest<void>(`/api/software-normalisation-rules/${id}`, { method: 'DELETE' }),

  listInstalls: (filter: InstalledSoftwareFilter = {}) =>
    apiRequest<InstalledSoftwarePage>(`/api/installed-software?${installFilterToQuery(filter)}`),
  listUnrecognised: (limit = 25) =>
    apiRequest<UnrecognisedSoftware[]>(`/api/installed-software/unrecognised?limit=${limit}`),
  // Re-running the catalogue writes, so a rule added today reaches the inventory imported last month.
  normalise: () =>
    apiRequest<SoftwareNormalisationRun>('/api/installed-software/normalisations', { method: 'POST' }),

  listPools: (filter: LicensePoolFilter = {}) =>
    apiRequest<LicensePoolPage>(`/api/license-pools?${poolFilterToQuery(filter)}`),
  createPool: (input: LicensePoolInput) =>
    apiRequest<LicensePool>('/api/license-pools', { method: 'POST', body: JSON.stringify(input) }),
  updatePool: (id: string, input: LicensePoolInput & { isActive: boolean }) =>
    apiRequest<LicensePool>(`/api/license-pools/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  deletePool: (id: string) => apiRequest<void>(`/api/license-pools/${id}`, { method: 'DELETE' }),

  getCompliance: (state?: SoftwareComplianceState, search?: string) => {
    const query = new URLSearchParams()
    if (state) query.set('state', state)
    if (search?.trim()) query.set('search', search.trim())
    return apiRequest<SoftwareCompliance>(`/api/software-compliance?${query.toString()}`)
  },
  runCompliance: () => apiRequest<SoftwareComplianceRun>('/api/software-compliance/runs', { method: 'POST' }),

  // The preview and the commit take the same file and answer the same report.
  previewImport: (file: File) => apiUpload<SoftwareImportReport>('/api/software-imports/preview', formData(file)),
  commitImport: (file: File) => apiUpload<SoftwareImportReport>('/api/software-imports/commit', formData(file)),
}
