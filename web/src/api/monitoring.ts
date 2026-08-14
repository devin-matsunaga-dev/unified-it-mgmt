import { apiRequest } from './client'

export type AlertSeverity = 'Ok' | 'Warning' | 'Critical'
export type AlertStatus = 'Open' | 'Cleared'
export type AlertSuppression = 'None' | 'Maintenance' | 'Flapping' | 'RootCause'
export type DeviceStatus = 'Ok' | 'Warning' | 'Critical' | 'Unknown' | 'Disabled'
export type CheckType = 'Icmp' | 'Snmp' | 'Tcp' | 'Http' | 'Tls'
export type MetricResolution = 'Auto' | 'Raw' | 'FiveMinute'
export type MetricAggregation = 'Avg' | 'Min' | 'Max'

/**
 * One row of the alert board. Everything from `ciName` down is read live through the WP-3.7 ports on
 * every request and is not stored on the alert — a reassignment reaches every board at once.
 */
export type Alert = {
  id: string
  deviceId: string
  ciId: string
  checkId: string
  ruleId: string
  metricName: string
  severity: AlertSeverity
  status: AlertStatus
  summary: string
  lastValue: number | null
  threshold: number | null
  consecutiveBreaches: number
  isFlapping: boolean
  suppression: AlertSuppression
  /** The alert this one is filed under while a dependency of its CI is failing too (WP-5.1). */
  rootCauseAlertId: string | null
  /** How many open alerts are filed under this one. Zero for all but a root cause. */
  impactedCount: number
  raisedAt: string
  lastObservedAt: string
  clearedAt: string | null
  pollerName: string
  acknowledgedAt: string | null
  acknowledgedBy: string | null
  acknowledgedByName: string | null
  deviceAddress: string | null
  checkName: string | null
  ciFound: boolean
  ciName: string | null
  ciType: string | null
  assetTag: string | null
  lifecycleState: string | null
  ownerName: string | null
  siteName: string | null
  departmentName: string | null
  warrantyExpiresAt: string | null
  warrantyStatus: string | null
  warrantyDaysRemaining: number | null
  contractName: string | null
}

export type LinkedTicket = {
  ticketId: string
  number: string
  title: string
  status: string
  priority: string
  createdAt: string
}

/** One alert suppressed underneath another, as the root cause's drawer lists it (WP-5.1). */
export type ImpactedAlert = {
  alertId: string
  deviceId: string
  ciId: string
  ciName: string | null
  ciType: string | null
  ruleId: string
  severity: AlertSeverity
  suppression: AlertSuppression
  summary: string
  raisedAt: string
}

export type AlertDetail = {
  alert: Alert
  openTickets: LinkedTicket[]
  impacted: ImpactedAlert[]
}

export type AlertCounts = { open: number; critical: number; warning: number; unacknowledged: number }

export type AlertPage = {
  items: Alert[]
  total: number
  page: number
  pageSize: number
  counts: AlertCounts
}

export type AlertFilter = {
  status?: AlertStatus
  severity?: AlertSeverity
  deviceId?: string
  ciId?: string
  acknowledged?: boolean
  page?: number
  pageSize?: number
}

export type DeviceStatusTile = {
  deviceId: string
  ciId: string
  ciName: string | null
  ciType: string | null
  siteName: string | null
  address: string
  pollerGroup: string
  isEnabled: boolean
  status: DeviceStatus
  severity: AlertSeverity
  openAlerts: number
  criticalAlerts: number
  warningAlerts: number
  acknowledgedAlerts: number
  checkCount: number
  headline: string | null
  worstAlertRaisedAt: string | null
  lastTelemetryAt: string | null
}

export type StatusBoardCounts = {
  devices: number
  ok: number
  warning: number
  critical: number
  unknown: number
  disabled: number
}

export type StatusBoard = {
  items: DeviceStatusTile[]
  total: number
  page: number
  pageSize: number
  counts: StatusBoardCounts
}

export type MonitoredDevice = {
  id: string
  ciId: string
  ciName: string | null
  ciType: string | null
  ciLifecycleState: string | null
  siteName: string | null
  address: string
  pollerGroup: string
  isEnabled: boolean
  notes: string | null
  checkCount: number
}

export type DeviceCheck = {
  id: string
  deviceId: string
  type: CheckType
  name: string
  intervalSeconds: number
  timeoutSeconds: number
  warningThreshold: number | null
  criticalThreshold: number | null
  comparison: 'GreaterThan' | 'LessThan'
  isEnabled: boolean
}

/** One entry of the chart's metric picker — per metric *and* check, because that is what a series is. */
export type DeviceMetricSummary = {
  metric: string
  unit: string | null
  checkId: string
  checkName: string | null
  lastObservedAt: string
  lastValue: number
}

export type MetricPoint = {
  timestamp: string
  value: number
  minValue: number
  maxValue: number
  sampleCount: number
}

export type MetricSeries = {
  deviceId: string
  metric: string
  checkId: string | null
  unit: string | null
  from: string
  to: string
  resolution: Exclude<MetricResolution, 'Auto'>
  aggregation: MetricAggregation
  bucketSeconds: number
  points: MetricPoint[]
}

/**
 * One interface of one device, as the last poll found it. Every number is nullable because a rate is
 * a subtraction between two polls: a poller that has seen this port once has its name and its status
 * and no traffic, which is different from a port carrying nothing.
 */
export type DeviceInterface = {
  ifIndex: number
  name: string
  alias: string | null
  macAddress: string | null
  interfaceType: number | null
  adminStatus: InterfaceStatus
  operStatus: InterfaceStatus
  speedBitsPerSecond: number | null
  bitsInPerSecond: number | null
  bitsOutPerSecond: number | null
  utilisationPercent: number | null
  errorsInPerSecond: number | null
  errorsOutPerSecond: number | null
  discardsInPerSecond: number | null
  discardsOutPerSecond: number | null
  checkId: string
  /** What to prepend to a field name for this port's series — `interface.3.`. Built by the API. */
  metricPrefix: string
  observedAt: string
}

/** IF-MIB's ifOperStatus and ifAdminStatus, which share one enumeration. */
export type InterfaceStatus =
  | 'Unknown' | 'Up' | 'Down' | 'Testing' | 'NotReported' | 'Dormant' | 'NotPresent' | 'LowerLayerDown'

export type DeviceInventory = {
  deviceId: string
  facts: { name: string; value: string; observedAt: string }[]
}

function query(params: Record<string, string | number | boolean | undefined>) {
  const search = new URLSearchParams()
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== '') search.set(key, String(value))
  }
  const rendered = search.toString()
  return rendered ? `?${rendered}` : ''
}

export const monitoringApi = {
  statusBoard: (params: { search?: string; pollerGroup?: string; page?: number; pageSize?: number } = {}) =>
    apiRequest<StatusBoard>(`/api/monitoring/status-board${query(params)}`),

  listAlerts: (filter: AlertFilter = {}) => apiRequest<AlertPage>(`/api/alerts${query(filter)}`),

  getAlert: (id: string) => apiRequest<AlertDetail>(`/api/alerts/${id}`),

  acknowledgeAlert: (id: string) =>
    apiRequest<Alert>(`/api/alerts/${id}/acknowledgements`, { method: 'POST' }),

  getDevice: (id: string) => apiRequest<MonitoredDevice>(`/api/monitored-devices/${id}`),

  listChecks: (deviceId: string) =>
    apiRequest<DeviceCheck[]>(`/api/monitored-devices/${deviceId}/checks`),

  listMetrics: (deviceId: string) =>
    apiRequest<DeviceMetricSummary[]>(`/api/monitored-devices/${deviceId}/metrics`),

  getSeries: (deviceId: string, params: {
    metric: string
    checkId?: string
    from: string
    to: string
    resolution?: MetricResolution
    aggregation?: MetricAggregation
  }) => apiRequest<MetricSeries>(`/api/monitored-devices/${deviceId}/metrics/series${query(params)}`),

  listInterfaces: (deviceId: string) =>
    apiRequest<DeviceInterface[]>(`/api/monitored-devices/${deviceId}/interfaces`),

  getInventory: (deviceId: string) =>
    apiRequest<DeviceInventory>(`/api/monitored-devices/${deviceId}/inventory`),
}
