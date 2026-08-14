import { apiDownload, apiRequest, apiUpload } from './client'

export type CiType = 'Hardware' | 'Server' | 'NetworkDevice' | 'Software' | 'Virtual' | 'Logical'
export type CiAttributeKind = 'Text' | 'Integer' | 'IpAddress'
export type CiCustomFieldType = 'Text' | 'Number' | 'Date' | 'Select'

/** A fixed attribute of a CI type — a real column, not a runtime field. */
export type CiAttributeDefinition = {
  key: string
  label: string
  kind: CiAttributeKind
  isRequired: boolean
}

export type CiCustomField = {
  id: string
  ciType: CiType
  key: string
  label: string
  type: CiCustomFieldType
  isRequired: boolean
  options: string[]
  sortOrder: number
}

/** Both halves of a type's form: fixed attributes plus whatever an admin has added at runtime. */
export type CiTypeSchema = {
  type: CiType
  attributes: CiAttributeDefinition[]
  customFields: CiCustomField[]
}

export type CiCustomFieldValue = {
  fieldId: string
  key: string
  label: string
  type: CiCustomFieldType
  value: string
}

export type CiLifecycleState = 'Ordered' | 'InStock' | 'Deployed' | 'InRepair' | 'Retired' | 'Disposed'
export type CiAssignmentAction = 'CheckOut' | 'CheckIn' | 'Transfer' | 'Relocate'

/** Who holds a CI and where it lives. Names are snapshots taken when it was assigned. */
export type CiOwnership = {
  ownerUserId: string | null
  ownerName: string | null
  departmentId: string | null
  departmentName: string | null
  siteId: string | null
  siteName: string | null
  assignedAt: string | null
}

export type CiLifecycleHistory = {
  id: string
  ciId: string
  fromState: CiLifecycleState
  toState: CiLifecycleState
  note: string | null
  actorId: string
  occurredAt: string
}

export type CiAssignmentEntry = {
  id: string
  ciId: string
  action: CiAssignmentAction
  fromOwnerUserId: string | null
  fromOwnerName: string | null
  toOwnerUserId: string | null
  toOwnerName: string | null
  departmentId: string | null
  departmentName: string | null
  siteId: string | null
  siteName: string | null
  note: string | null
  actorId: string
  occurredAt: string
}

/** The lifecycle graph as data: the server owns the guard, the form only renders it. */
export type CiLifecycleStateInfo = { state: CiLifecycleState; allowedTargets: CiLifecycleState[] }

export type CiRelationshipType = 'RunsOn' | 'ConnectsTo' | 'DependsOn' | 'HostedOn'

/** One edge, with both ends named so a list needs no second call. */
export type CiRelationship = {
  id: string
  sourceCiId: string
  sourceCiName: string
  sourceCiType: CiType
  targetCiId: string
  targetCiName: string
  targetCiType: CiType
  type: CiRelationshipType
  description: string | null
  createdBy: string
  createdAt: string
}

/** Upstream is what the CI depends on; downstream is what depends on it. */
export type CiRelationships = { ciId: string; upstream: CiRelationship[]; downstream: CiRelationship[] }

export type CiGraphNode = {
  id: string
  type: CiType
  name: string
  assetTag: string | null
  lifecycleState: CiLifecycleState
  isActive: boolean
  depth: number
}

export type CiGraphEdge = { id: string; sourceCiId: string; targetCiId: string; type: CiRelationshipType }

export type CiGraph = {
  rootCiId: string
  direction: 'Ancestors' | 'Descendants'
  maxDepth: number
  maxDepthReached: boolean
  containsCycle: boolean
  nodes: CiGraphNode[]
  edges: CiGraphEdge[]
}

/**
 * Where one open ticket stands against its resolution target (WP-5.2). Null on a ticket whose priority
 * matched no SLA policy when it was raised — nothing is on the clock, which is a real state and not a
 * missing read.
 */
export type SlaExposure = {
  policyName: string
  resolutionDueAt: string
  remainingSeconds: number
  breached: boolean
  atRisk: boolean
}

export type ImpactedCi = {
  ciId: string
  name: string
  type: CiType
  lifecycleState: CiLifecycleState
  isActive: boolean
  /** Hops from the CI that failed; the CI itself is 0, because it is part of its own outage. */
  depth: number
  ownerUserId: string | null
  ownerName: string | null
  departmentId: string | null
  departmentName: string | null
  siteName: string | null
  openTicketCount: number
}

export type ImpactedTicket = {
  ticketId: string
  number: string
  title: string
  status: string
  priority: string
  createdAt: string
  /** The affected CI nearest the failure that this ticket is linked to. */
  ciId: string
  ciName: string
  sla: SlaExposure | null
}

export type ImpactedDepartment = { departmentId: string; name: string; ciCount: number; openTicketCount: number }

export type ImpactedUser = { userId: string; name: string; ciCount: number; openTicketCount: number }

export type CiImpactSummary = {
  ciCount: number
  directCiCount: number
  openTicketCount: number
  breachedSlaCount: number
  atRiskSlaCount: number
  nextSlaDueAt: string | null
  affectedUserCount: number
  affectedDepartmentCount: number
  cisWithoutDepartment: number
  cisTruncated: boolean
  ticketsTruncated: boolean
}

/** "What breaks if this dies": the graph, what is already open on it, and who feels it. */
export type CiImpact = {
  rootCiId: string
  rootCiName: string
  rootCiType: CiType
  maxDepth: number
  maxDepthReached: boolean
  containsCycle: boolean
  summary: CiImpactSummary
  cis: ImpactedCi[]
  tickets: ImpactedTicket[]
  departments: ImpactedDepartment[]
  users: ImpactedUser[]
}

/**
 * What covers a CI. Contract fields are read live from the contract rather than snapshotted, so a
 * renamed contract reaches every CI it covers at once.
 */
export type CiCoverage = {
  contractId: string | null
  contractName: string | null
  contractNumber: string | null
  vendorName: string | null
  contractEndDate: string | null
  purchaseDate: string | null
  warrantyExpiresAt: string | null
  warrantyStatus: 'Active' | 'ExpiringSoon' | 'Expired' | null
  warrantyDaysRemaining: number | null
}

export type Ci = {
  id: string
  type: CiType
  name: string
  assetTag: string | null
  serialNumber: string | null
  description: string | null
  isActive: boolean
  lifecycleState: CiLifecycleState
  ownership: CiOwnership
  coverage: CiCoverage
  attributes: Record<string, string>
  customFields: CiCustomFieldValue[]
  createdAt: string
  updatedAt: string
}

export type CiPage = { items: Ci[]; total: number; page: number; pageSize: number }

export type CiFilter = {
  type?: CiType
  search?: string
  isActive?: boolean
  lifecycleState?: CiLifecycleState
  ownerUserId?: string
  departmentId?: string
  siteId?: string
  contractId?: string
  warrantyExpiringWithinDays?: number
  page?: number
  pageSize?: number
}

export type CreateCiInput = {
  type: CiType
  name: string
  assetTag: string | null
  serialNumber: string | null
  description: string | null
  attributes: Record<string, string>
  customFields: Record<string, string>
  lifecycleState?: CiLifecycleState
}

export type AssignCiInput = {
  ownerUserId: string | null
  departmentId: string | null
  siteId: string | null
  note: string | null
}

export type UpdateCiInput = Omit<CreateCiInput, 'type'> & { isActive: boolean }

export type CiImportTargetKind = 'Core' | 'Attribute' | 'CustomField'

/**
 * A column an import can fill. Attribute and custom-field keys are prefixed so they cannot collide.
 * `types` is sent only by a mixed-type import, where one column belongs to several CI types and is
 * required by only some of them.
 */
export type CiImportTarget = {
  key: string
  label: string
  isRequired: boolean
  kind: CiImportTargetKind
  types?: { type: CiType; isRequired: boolean }[] | null
}

export type CiImportColumns = {
  fileName: string
  headers: string[]
  sampleRows: string[][]
  rowCount: number
  targets: CiImportTarget[]
  /** targetKey → header, the server's first guess at the mapping. */
  suggestedMapping: Record<string, string>
}

export type CiImportAction = 'Create' | 'Update' | 'Skip' | 'Error'

/** Where a row's CI type came from. `Inferred` is a guess the operator has to see before committing. */
export type CiImportTypeSource = 'Fixed' | 'Column' | 'Inferred'

export type CiImportRowResult = {
  lineNumber: number
  action: CiImportAction
  name: string | null
  assetTag: string | null
  serialNumber: string | null
  matchedCiId: string | null
  errors: string[]
  type?: CiType | null
  typeSource?: CiImportTypeSource | null
}

export type CiImportReport = {
  isDryRun: boolean
  totalRows: number
  created: number
  updated: number
  skipped: number
  failed: number
  rows: CiImportRowResult[]
}

/** A file is one CI type, or 'Mixed' — every row then states or implies its own. */
export type CiImportType = CiType | 'Mixed'

/**
 * The chosen type plus targetKey → header for each mapped column. `acceptInferredTypes` is the
 * operator confirming the guesses the dry run showed; the server refuses a commit without it.
 */
export type CiImportMapping = {
  type: CiImportType
  columns: Record<string, string>
  acceptInferredTypes?: boolean
}

/** Stock label formats. Standard is 3 × 7 on A4, Small is 4 × 12; a single label prints on its own page. */
export type CiLabelSize = 'Standard' | 'Small'

export const ciLabelSizes: { value: CiLabelSize; label: string; hint: string }[] = [
  { value: 'Standard', label: 'Standard', hint: '63.5 × 33.9 mm — 3 × 7 per A4 sheet' },
  { value: 'Small', label: 'Small', hint: '45.7 × 21.2 mm — 4 × 12 per A4 sheet' },
]

export type CiOwnershipChange ={ ownerUserId: string | null; departmentId: string | null; siteId: string | null }

export type BulkEditCisInput = {
  ciIds: string[]
  ownership?: CiOwnershipChange
  lifecycleState?: CiLifecycleState
  note?: string | null
}

export type BulkEditRowResult = { ciId: string; name: string | null; succeeded: boolean; error: string | null }

export type BulkEditReport = { total: number; succeeded: number; failed: number; rows: BulkEditRowResult[] }

export const ciTypes: CiType[] = ['Hardware', 'Server', 'NetworkDevice', 'Software', 'Virtual', 'Logical']

const ciTypeLabels: Record<CiType, string> = {
  Hardware: 'Hardware',
  Server: 'Server',
  NetworkDevice: 'Network device',
  Software: 'Software',
  Virtual: 'Virtual machine',
  Logical: 'Logical',
}

export function ciTypeLabel(type: string) {
  return ciTypeLabels[type as CiType] ?? type
}

export function ciFilterToQuery(filter: CiFilter) {
  const query = new URLSearchParams()
  if (filter.type) query.set('type', filter.type)
  if (filter.search?.trim()) query.set('search', filter.search.trim())
  if (filter.isActive !== undefined) query.set('isActive', String(filter.isActive))
  if (filter.lifecycleState) query.set('lifecycleState', filter.lifecycleState)
  if (filter.ownerUserId) query.set('ownerUserId', filter.ownerUserId)
  if (filter.departmentId) query.set('departmentId', filter.departmentId)
  if (filter.siteId) query.set('siteId', filter.siteId)
  if (filter.contractId) query.set('contractId', filter.contractId)
  if (filter.warrantyExpiringWithinDays !== undefined) query.set('warrantyExpiringWithinDays', String(filter.warrantyExpiringWithinDays))
  query.set('page', String(filter.page ?? 1))
  query.set('pageSize', String(filter.pageSize ?? 25))
  return query.toString()
}

function formData(file: File, [name, value]: [string, string]) {
  const body = new FormData()
  body.append('file', file)
  body.append(name, value)
  return body
}

export const assetsApi = {
  listCis: (filter: CiFilter = {}) => apiRequest<CiPage>(`/api/cis?${ciFilterToQuery(filter)}`),
  getCi: (id: string) => apiRequest<Ci>(`/api/cis/${id}`),
  createCi: (input: CreateCiInput) => apiRequest<Ci>('/api/cis', { method: 'POST', body: JSON.stringify(input) }),
  updateCi: (id: string, input: UpdateCiInput) => apiRequest<Ci>(`/api/cis/${id}`, { method: 'PUT', body: JSON.stringify(input) }),
  deleteCi: (id: string) => apiRequest<void>(`/api/cis/${id}`, { method: 'DELETE' }),
  listLifecycleStates: () => apiRequest<CiLifecycleStateInfo[]>('/api/ci-lifecycle-states'),
  transitionCi: (id: string, targetState: CiLifecycleState, note: string | null) =>
    apiRequest<Ci>(`/api/cis/${id}/lifecycle-transitions`, { method: 'POST', body: JSON.stringify({ targetState, note }) }),
  getLifecycleHistory: (id: string) => apiRequest<CiLifecycleHistory[]>(`/api/cis/${id}/lifecycle-transitions`),
  assignCi: (id: string, input: AssignCiInput) => apiRequest<Ci>(`/api/cis/${id}/assignment`, { method: 'PUT', body: JSON.stringify(input) }),
  getAssignments: (id: string) => apiRequest<CiAssignmentEntry[]>(`/api/cis/${id}/assignments`),
  getRelationships: (id: string) => apiRequest<CiRelationships>(`/api/cis/${id}/relationships`),
  createRelationship: (id: string, input: { targetCiId: string; type: CiRelationshipType; description?: string | null }) =>
    apiRequest<CiRelationship>(`/api/cis/${id}/relationships`, { method: 'POST', body: JSON.stringify(input) }),
  deleteRelationship: (relationshipId: string) => apiRequest<void>(`/api/ci-relationships/${relationshipId}`, { method: 'DELETE' }),
  getImpactedBy: (id: string, maxDepth = 3) => apiRequest<CiGraph>(`/api/cis/${id}/impacted-by?maxDepth=${maxDepth}`),
  getAncestors: (id: string, maxDepth = 3) => apiRequest<CiGraph>(`/api/cis/${id}/ancestors?maxDepth=${maxDepth}`),
  getImpact: (id: string, maxDepth = 5) => apiRequest<CiImpact>(`/api/cis/${id}/impact?maxDepth=${maxDepth}`),
  listTypeSchemas: () => apiRequest<CiTypeSchema[]>('/api/ci-type-schemas'),
  // Each step re-sends the file the browser already holds, so a half-mapped import is never parked
  // on the server between steps.
  inspectImport: (file: File, type: CiImportType) =>
    apiUpload<CiImportColumns>('/api/ci-imports/columns', formData(file, ['type', type])),
  previewImport: (file: File, mapping: CiImportMapping) =>
    apiUpload<CiImportReport>('/api/ci-imports/preview', formData(file, ['mapping', JSON.stringify(mapping)])),
  commitImport: (file: File, mapping: CiImportMapping) =>
    apiUpload<CiImportReport>('/api/ci-imports/commit', formData(file, ['mapping', JSON.stringify(mapping)])),
  // Labels come back as PDFs, so they go through apiDownload rather than the JSON client.
  getCiLabel: (id: string, size: CiLabelSize) => apiDownload(`/api/cis/${id}/label?size=${size}`),
  getCiLabelSheet: (ciIds: string[], size: CiLabelSize) =>
    apiDownload('/api/ci-labels/sheets', { method: 'POST', body: JSON.stringify({ ciIds, size }) }),
  // The scan page's one call: a label URL, a bare id, an asset tag, or a serial number in; the CI out.
  lookupCi: (code: string) => apiRequest<Ci>(`/api/cis/lookup?code=${encodeURIComponent(code)}`),
  bulkEditCis: (input: BulkEditCisInput) =>
    apiRequest<BulkEditReport>('/api/cis/bulk-edit', { method: 'POST', body: JSON.stringify(input) }),
  createCustomField: (input: { ciType: CiType; key: string; label: string; type: CiCustomFieldType; isRequired: boolean; options?: string[]; sortOrder?: number }) =>
    apiRequest<CiCustomField>('/api/ci-custom-fields', { method: 'POST', body: JSON.stringify(input) }),
  deleteCustomField: (id: string) => apiRequest<void>(`/api/ci-custom-fields/${id}`, { method: 'DELETE' }),
}

// ---- Discovery review queue (WP-4.2) --------------------------------------------------------

export type DiscoveredDeviceStatus = 'Pending' | 'Matched' | 'Approved' | 'Rejected'

/**
 * Which signal placed a discovery against a CI, strongest first. `Ambiguous` is not a match: two CIs
 * claimed the device and the card asks a human which.
 */
export type DiscoveryMatchRule =
  | 'None' | 'Ledger' | 'MonitoredAddress' | 'ManagementIp' | 'Hostname' | 'Name' | 'Ambiguous'

export type DiscoveredSnmp = {
  sysName: string | null
  sysDescription: string | null
  sysObjectId: string | null
  sysLocation: string | null
  sysContact: string | null
  uptimeSeconds: number | null
}

export type DiscoveredNeighbour = {
  protocol: string
  localPort: string | null
  remoteSystemName: string | null
  remotePort: string | null
  remoteAddress: string | null
}

export type DiscoveryContender = { ciId: string; name: string; type: CiType }

export type DiscoveredDevice = {
  id: string
  identityKey: string
  address: string
  hostname: string | null
  respondedToPing: boolean
  openPorts: number[]
  snmp: DiscoveredSnmp | null
  neighbours: DiscoveredNeighbour[]
  discoveryName: string
  scanProfileId: string
  scanProfileName: string
  status: DiscoveredDeviceStatus
  ciId: string | null
  ciName: string | null
  matchRule: DiscoveryMatchRule
  contenders: DiscoveryContender[]
  suggestedType: CiType
  suggestedName: string
  suggestedAttributes: Record<string, string>
  firstSeenAt: string
  lastSeenAt: string
  sightingCount: number
  reviewedBy: string | null
  reviewedAt: string | null
  reviewNote: string | null
}

export type DiscoveredDevicePage = { items: DiscoveredDevice[]; total: number; page: number; pageSize: number }

/** `status` omitted means the pending queue; `all` is the explicit way to see the history. */
export type DiscoveredDeviceFilter = {
  status?: DiscoveredDeviceStatus | 'all'
  search?: string
  page?: number
  pageSize?: number
}

export type ApproveDiscoveredDeviceInput = {
  type?: CiType
  name?: string
  assetTag?: string | null
  serialNumber?: string | null
  description?: string | null
  attributes?: Record<string, string>
  ciId?: string
  enrollMonitoring?: boolean
  pollerGroup?: string | null
  note?: string | null
}

/** What discovery last observed about a CI, beside — never written into — what the CMDB records. */
export type CiDiscoveryFacts = {
  ciId: string
  address: string
  hostname: string | null
  respondedToPing: boolean
  openPorts: number[]
  snmp: DiscoveredSnmp | null
  neighbours: DiscoveredNeighbour[]
  discoveryName: string
  scanProfileName: string
  firstSeenAt: string
  lastSeenAt: string
  sightingCount: number
}

const matchRuleLabels: Record<DiscoveryMatchRule, string> = {
  None: 'No match',
  Ledger: 'Decided on an earlier scan',
  MonitoredAddress: 'Already monitored at this address',
  ManagementIp: 'Management IP on record',
  Hostname: 'Hostname on record',
  Name: 'CI named after this device',
  Ambiguous: 'Two CIs claim it',
}

export function discoveryMatchRuleLabel(rule: string) {
  return matchRuleLabels[rule as DiscoveryMatchRule] ?? rule
}

export function discoveredDeviceFilterToQuery(filter: DiscoveredDeviceFilter) {
  const query = new URLSearchParams()
  if (filter.status) query.set('status', filter.status)
  if (filter.search?.trim()) query.set('search', filter.search.trim())
  query.set('page', String(filter.page ?? 1))
  query.set('pageSize', String(filter.pageSize ?? 25))
  return query.toString()
}

export const discoveryApi = {
  listDiscovered: (filter: DiscoveredDeviceFilter = {}) =>
    apiRequest<DiscoveredDevicePage>(`/api/discovered-devices?${discoveredDeviceFilterToQuery(filter)}`),
  getDiscovered: (id: string) => apiRequest<DiscoveredDevice>(`/api/discovered-devices/${id}`),
  approveDiscovered: (id: string, input: ApproveDiscoveredDeviceInput) =>
    apiRequest<DiscoveredDevice>(`/api/discovered-devices/${id}/approvals`, { method: 'POST', body: JSON.stringify(input) }),
  rejectDiscovered: (id: string, note: string | null) =>
    apiRequest<DiscoveredDevice>(`/api/discovered-devices/${id}/rejections`, { method: 'POST', body: JSON.stringify({ note }) }),
  getCiDiscoveryFacts: (ciId: string) => apiRequest<CiDiscoveryFacts>(`/api/cis/${ciId}/discovery-facts`),
}
