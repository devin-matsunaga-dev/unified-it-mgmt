import { useQuery } from '@tanstack/react-query'
import { Check, ExternalLink, X } from 'lucide-react'
import { Link } from 'react-router-dom'
import { monitoringApi } from '../../api/monitoring'
import { Button } from '../../components/ui/Button'
import { SeverityPill, formatAge, formatLocal } from './severity'

/**
 * Detail-peek drawer (DESIGN.md §6) for one alert, and the caller `GET /api/alerts/{id}` was built
 * for in WP-3.7: it is the only read that carries the open related tickets, which cost a query per CI
 * and are deliberately absent from the board's rows.
 *
 * It is also where a WP-3.10 deep link lands. A Teams or Slack message links to
 * `/monitoring/alerts?alertId=…`, so the operator arrives on the board with this open on the alert
 * they were paged about rather than on a list they have to search.
 */
export function AlertDetailDrawer({ alertId, onClose, onAcknowledge, acknowledging }: {
  alertId: string | null
  onClose: () => void
  onAcknowledge: (id: string) => void
  acknowledging: boolean
}) {
  const detail = useQuery({
    queryKey: ['monitoring', 'alert', alertId],
    queryFn: () => monitoringApi.getAlert(alertId!),
    enabled: Boolean(alertId),
  })

  if (!alertId) return null

  const alert = detail.data?.alert
  const tickets = detail.data?.openTickets ?? []

  return <div className="fixed inset-0 z-40 flex justify-end">
    <button type="button" aria-label="Close alert details" onClick={onClose}
      className="absolute inset-0 bg-slate-900/30 dark:bg-slate-950/50" />
    <aside role="dialog" aria-modal="true" aria-label="Alert details"
      className="relative flex h-full w-full max-w-[480px] flex-col overflow-y-auto border-l border-slate-200 bg-white shadow-lg dark:border-slate-800 dark:bg-slate-900">
      <header className="flex items-start justify-between gap-3 border-b border-slate-200 p-5 dark:border-slate-800">
        <div className="min-w-0">
          <p className="text-[13px] text-slate-500">Alert</p>
          <h2 className="truncate text-lg font-semibold">{alert?.summary ?? 'Loading…'}</h2>
        </div>
        <Button variant="secondary" className="h-9 w-9 shrink-0 px-0" aria-label="Close" onClick={onClose}>
          <X size={16} />
        </Button>
      </header>

      {detail.isLoading
        ? <div className="space-y-2 p-5">
          {[0, 1, 2, 3, 4].map((key) => <div key={key} aria-label="Loading alert"
            className="h-10 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}
        </div>
        : detail.isError || !alert
          // "This alert has no details" would be a claim about the alert. A failed read is a fact
          // about the request, and the two must not read the same (the WP-2.11 rule).
          ? <p className="p-5 text-sm text-slate-500">
            This alert could not be loaded. It may have been deleted, or the request failed.
          </p>
          : <div className="space-y-6 p-5">
            <div className="flex flex-wrap items-center gap-2">
              <SeverityPill severity={alert.severity} />
              <span className="rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600 dark:bg-slate-500/15 dark:text-slate-400">
                {alert.status}
              </span>
              {alert.isFlapping && <span className="rounded-md bg-amber-100 px-2 py-0.5 text-xs font-medium text-amber-700 dark:bg-amber-500/15 dark:text-amber-300">Flapping</span>}
              {alert.suppression !== 'None' && <span className="rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600 dark:bg-slate-500/15 dark:text-slate-400">Suppressed: {alert.suppression}</span>}
            </div>

            <Section title="What is wrong">
              <Row label="Check" value={alert.checkName ?? alert.ruleId} />
              <Row label="Rule" value={alert.ruleId} />
              <Row label="Metric" value={alert.metricName} />
              <Row label="Value" value={alert.lastValue === null ? '—' : String(alert.lastValue)} />
              <Row label="Threshold" value={alert.threshold === null ? '—' : String(alert.threshold)} />
              <Row label="Breaches in a row" value={String(alert.consecutiveBreaches)} />
              <Row label="Raised" value={`${formatAge(alert.raisedAt)} · ${formatLocal(alert.raisedAt)}`} />
              {alert.clearedAt && <Row label="Cleared" value={formatLocal(alert.clearedAt)} />}
            </Section>

            <Section title="Asset">
              {alert.ciFound
                ? <>
                  <Row label="Name" value={
                    <Link to={`/assets/${alert.ciId}`} className="text-blue-600 hover:underline dark:text-blue-400">
                      {alert.ciName}
                    </Link>} />
                  <Row label="Type" value={alert.ciType ?? '—'} />
                  <Row label="Owner" value={alert.ownerName ?? 'Nobody holds this asset'} />
                  <Row label="Location" value={alert.siteName ?? '—'} />
                  <Row label="Department" value={alert.departmentName ?? '—'} />
                  <Row label="Warranty" value={alert.warrantyStatus
                    ? `${alert.warrantyStatus}${alert.warrantyDaysRemaining === null ? '' : ` · ${alert.warrantyDaysRemaining} days`}`
                    : 'None recorded'} />
                  <Row label="Contract" value={alert.contractName ?? '—'} />
                </>
                : <p className="text-sm text-slate-500">Not found in the CMDB.</p>}
              <Row label="Device" value={
                <Link to={`/monitoring/devices/${alert.deviceId}`} className="text-blue-600 hover:underline dark:text-blue-400">
                  {alert.deviceAddress ?? alert.deviceId}
                </Link>} />
            </Section>

            <Section title={`Open tickets for this asset (${tickets.length})`}>
              {tickets.length === 0
                ? <p className="text-sm text-slate-500">Nothing open is linked to this asset.</p>
                : <ul className="space-y-2">
                  {tickets.map((ticket) => <li key={ticket.ticketId}>
                    <Link to={`/tickets/${ticket.ticketId}`}
                      className="flex items-center gap-2 text-sm text-blue-600 hover:underline dark:text-blue-400">
                      <ExternalLink size={14} />
                      <span className="font-medium">{ticket.number}</span>
                      <span className="truncate text-slate-600 dark:text-slate-300">{ticket.title}</span>
                    </Link>
                    <p className="ml-6 text-[13px] text-slate-500">{ticket.status} · {ticket.priority}</p>
                  </li>)}
                </ul>}
            </Section>

            <div className="border-t border-slate-200 pt-4 dark:border-slate-800">
              {alert.acknowledgedAt
                ? <p className="text-[13px] text-slate-500">
                  Acknowledged by {alert.acknowledgedByName ?? alert.acknowledgedBy} on {formatLocal(alert.acknowledgedAt)}.
                </p>
                : alert.status === 'Open'
                  ? <Button variant="secondary" className="h-9 px-3" disabled={acknowledging}
                    onClick={() => onAcknowledge(alert.id)}>
                    <Check size={16} />{acknowledging ? 'Acknowledging' : 'Acknowledge'}
                  </Button>
                  : <p className="text-[13px] text-slate-500">This alert cleared without being acknowledged.</p>}
            </div>
          </div>}
    </aside>
  </div>
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return <section className="space-y-2">
    <h3 className="text-sm font-medium">{title}</h3>
    <div className="space-y-1">{children}</div>
  </section>
}

function Row({ label, value }: { label: string; value: React.ReactNode }) {
  return <div className="flex items-baseline justify-between gap-4 text-sm">
    <span className="shrink-0 text-[13px] text-slate-500">{label}</span>
    <span className="min-w-0 truncate text-right">{value}</span>
  </div>
}
