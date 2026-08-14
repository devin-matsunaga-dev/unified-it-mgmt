import { apiRequest } from './client'
import type { CiLifecycleState, CiType } from './assets'

/**
 * The two halves of WP-4.6: where the CMDB and the network disagree about a CI, and where the CMDB
 * and the building disagree about an asset.
 */

export type DriftFindingKind = 'New' | 'Missing' | 'Changed'

export type DriftFinding = {
  field: string
  label: string
  kind: DriftFindingKind
  recordedValue: string | null
  observedValue: string | null
}

export type CiDrift = {
  ciId: string
  name: string
  type: CiType
  siteName: string | null
  address: string
  lastSeenAt: string
  findings: DriftFinding[]
}

/** A cable a scan saw that no relationship records — observed by WP-4.3 and written down by nobody. */
export type UnrecordedLink = {
  sourceCiId: string
  sourceCiName: string
  sourcePort: string | null
  targetCiId: string
  targetCiName: string
  targetPort: string | null
  protocols: string[]
  confirmedByBothEnds: boolean
}

export type DriftSummary = {
  cisObserved: number
  cisWithDrift: number
  changed: number
  new: number
  missing: number
  unrecordedLinks: number
  unmatchedDiscoveries: number
  staleAfterDays: number
  generatedAt: string
}

export type DriftReport = {
  summary: DriftSummary
  items: CiDrift[]
  unrecordedLinks: UnrecordedLink[]
  total: number
  page: number
  pageSize: number
}

export type DriftFilter = {
  kind?: DriftFindingKind
  field?: string
  siteId?: string
  staleAfterDays?: number
  page?: number
  pageSize?: number
}

const driftKindLabels: Record<DriftFindingKind, string> = {
  New: 'New',
  Missing: 'Missing',
  Changed: 'Changed',
}

const driftKindTones: Record<DriftFindingKind, string> = {
  // Changed is the finding somebody acts on, so it carries the strong colour; New is information and
  // Missing is a warning that something stopped answering.
  Changed: 'bg-red-50 text-red-600 dark:bg-red-500/15',
  Missing: 'bg-amber-50 text-amber-700 dark:bg-amber-500/15 dark:text-amber-400',
  New: 'bg-blue-50 text-blue-600 dark:bg-blue-500/15',
}

export function driftKindLabel(kind: DriftFindingKind) {
  return driftKindLabels[kind] ?? kind
}

export function driftKindTone(kind: DriftFindingKind) {
  return driftKindTones[kind] ?? 'bg-slate-100 text-slate-600 dark:bg-slate-500/15'
}

export function driftFilterToQuery(filter: DriftFilter) {
  const query = new URLSearchParams()
  if (filter.kind) query.set('kind', filter.kind)
  if (filter.field) query.set('field', filter.field)
  if (filter.siteId) query.set('siteId', filter.siteId)
  if (filter.staleAfterDays) query.set('staleAfterDays', String(filter.staleAfterDays))
  if (filter.page) query.set('page', String(filter.page))
  if (filter.pageSize) query.set('pageSize', String(filter.pageSize))
  return query.toString()
}

export type AuditSessionStatus = 'Open' | 'Closed'

export type AuditSessionSummary = {
  id: string
  name: string
  siteId: string | null
  siteName: string | null
  status: AuditSessionStatus
  openedBy: string
  openedAt: string
  closedBy: string | null
  closedAt: string | null
  note: string | null
  scanCount: number
}

/** The reconciled session: what it set out to walk against what it found. */
export type AuditSession = Omit<AuditSessionSummary, 'scanCount'> & {
  expectedCount: number
  scannedCount: number
  unscannedCount: number
  unexpectedCount: number
}

export type AuditSessionPage = {
  items: AuditSessionSummary[]
  total: number
  page: number
  pageSize: number
}

export type AuditItem = {
  ciId: string
  name: string
  type: CiType
  assetTag: string | null
  serialNumber: string | null
  lifecycleState: CiLifecycleState
  siteName: string | null
  ownerName: string | null
  scannedAt: string | null
  scannedBy: string | null
}

export type AuditUnexpectedReason = 'DifferentSite' | 'Disposed' | 'NotPhysical'

export type AuditUnexpectedItem = Omit<AuditItem, 'ownerName' | 'scannedAt' | 'scannedBy'> & {
  reason: AuditUnexpectedReason
  scannedAt: string
  scannedBy: string
}

export type AuditReport = {
  session: AuditSession
  scanned: AuditItem[]
  unscanned: AuditItem[]
  unexpected: AuditUnexpectedItem[]
  truncated: boolean
  generatedAt: string
}

export type AuditScan = {
  id: string
  sessionId: string
  ciId: string
  ciName: string
  ciType: CiType
  assetTag: string | null
  serialNumber: string | null
  code: string
  scannedBy: string
  scannedAt: string
  note: string | null
  /** Not an error: two people walking one rack is the normal case. */
  alreadyScanned: boolean
  expected: boolean
  unexpectedReason: AuditUnexpectedReason | null
}

const unexpectedReasons: Record<AuditUnexpectedReason, string> = {
  DifferentSite: 'Recorded at another site',
  Disposed: 'Recorded as disposed',
  NotPhysical: 'Not a physical asset',
}

export function unexpectedReasonLabel(reason: AuditUnexpectedReason) {
  return unexpectedReasons[reason] ?? reason
}

export const reconciliationApi = {
  getDrift: (filter: DriftFilter = {}) =>
    apiRequest<DriftReport>(`/api/drift?${driftFilterToQuery(filter)}`),

  listAuditSessions: (status?: AuditSessionStatus, page = 1, pageSize = 25) =>
    apiRequest<AuditSessionPage>(
      `/api/audit-sessions?${new URLSearchParams({
        ...(status ? { status } : {}),
        page: String(page),
        pageSize: String(pageSize),
      })}`),
  getAuditSession: (id: string) => apiRequest<AuditSession>(`/api/audit-sessions/${id}`),
  getAuditReport: (id: string) => apiRequest<AuditReport>(`/api/audit-sessions/${id}/report`),
  createAuditSession: (input: { name: string; siteId: string | null; note: string | null }) =>
    apiRequest<AuditSession>('/api/audit-sessions', { method: 'POST', body: JSON.stringify(input) }),
  recordAuditScan: (id: string, input: { code: string; note?: string | null }) =>
    apiRequest<AuditScan>(`/api/audit-sessions/${id}/scans`, { method: 'POST', body: JSON.stringify(input) }),
  removeAuditScan: (id: string, scanId: string) =>
    apiRequest<void>(`/api/audit-sessions/${id}/scans/${scanId}`, { method: 'DELETE' }),
  closeAuditSession: (id: string, note?: string | null) =>
    apiRequest<AuditSession>(`/api/audit-sessions/${id}/closure`, { method: 'POST', body: JSON.stringify({ note: note ?? null }) }),
}
