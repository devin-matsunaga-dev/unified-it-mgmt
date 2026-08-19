import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronLeft, ChevronRight, Network, Radar, Search } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import {
  ciTypeLabel,
  discoveryApi,
  discoveryMatchRuleLabel,
  hostnameSourceLabel,
  type DiscoveredDevice,
  type DiscoveredDeviceFilter,
  type DiscoveredDeviceStatus,
} from '../../api/assets'
import { Button } from '../../components/ui/Button'
import { DiscoveryApproveDialog } from './DiscoveryApproveDialog'
import { DiscoveryTabs } from './DiscoveryTabs'
import { usePageHeading } from '../../layout/pageHeading'

/**
 * What the scans found and nothing in the CMDB claims. The human end of WP-4.2: everything the matcher
 * could place is already placed, so a card on this page is by definition something that needs a person.
 */
const tabs: { value: DiscoveredDeviceStatus | 'all'; label: string; blurb: string }[] = [
  { value: 'Pending', label: 'Needs review', blurb: 'Nothing in the CMDB claims these.' },
  { value: 'Matched', label: 'Matched automatically', blurb: 'Placed against a CI without anyone having to look.' },
  { value: 'Approved', label: 'Approved', blurb: 'Somebody confirmed these and a CI exists.' },
  { value: 'Rejected', label: 'Ignored', blurb: 'Rejected once and skipped on every later scan.' },
]

const statusTones: Record<DiscoveredDeviceStatus, string> = {
  Pending: 'bg-amber-100 text-amber-700 dark:bg-amber-500/15 dark:text-amber-400',
  Matched: 'bg-blue-100 text-blue-700 dark:bg-blue-500/15 dark:text-blue-400',
  Approved: 'bg-green-100 text-green-700 dark:bg-green-500/15 dark:text-green-400',
  Rejected: 'bg-slate-100 text-slate-600 dark:bg-slate-500/15 dark:text-slate-400',
}

export function DiscoveryReviewPage() {
  const queryClient = useQueryClient()
  const [search, setSearch] = useState('')
  const [filter, setFilter] = useState<DiscoveredDeviceFilter>({ status: 'Pending', page: 1, pageSize: 25 })
  const [approving, setApproving] = useState<DiscoveredDevice | null>(null)
  // Rejecting is a two-step in-row confirm rather than a browser dialog, following WP-2.9's rule for
  // removing a relationship: the row is a clearer place to ask than a dialog that names nothing.
  const [confirmingReject, setConfirmingReject] = useState<string | null>(null)

  useEffect(() => {
    const timer = window.setTimeout(() => setFilter((current) => ({ ...current, search, page: 1 })), 200)
    return () => window.clearTimeout(timer)
  }, [search])

  // Polled, because what fills this queue happens somewhere else entirely: a scanner sweeps on its
  // own schedule or because somebody pressed "Scan now" on the next tab, and the events then travel
  // the bus. Without this the page shows "nothing needs review" until something forces a refetch —
  // which reads exactly like a scan that found nothing, and is the first thing anybody reports.
  const queue = useQuery({
    queryKey: ['discovered-devices', filter],
    queryFn: () => discoveryApi.listDiscovered(filter),
    refetchInterval: 10_000,
    placeholderData: keepPreviousData,
  })

  const reject = useMutation({
    mutationFn: (id: string) => discoveryApi.rejectDiscovered(id, null),
    onSuccess: async (device) => {
      await queryClient.invalidateQueries({ queryKey: ['discovered-devices'] })
      setConfirmingReject(null)
      toast.success(`${device.suggestedName} will be skipped on future scans`)
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const page = filter.page ?? 1
  const pageSize = filter.pageSize ?? 25
  const total = queue.data?.total ?? 0
  const lastPage = Math.max(Math.ceil(total / pageSize), 1)
  const activeTab = tabs.find((tab) => tab.value === filter.status) ?? tabs[0]
  usePageHeading({ title: 'Discovery review', subtitle: activeTab.blurb })

  return <div className="space-y-6">
    <DiscoveryTabs right={<div className="relative">
      <Search size={18} className="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-slate-400" />
      <input value={search} onChange={(event) => setSearch(event.target.value)} placeholder="Address, hostname or description"
        aria-label="Search discovered devices" className="input h-10 w-72 pl-10" />
    </div>} />

    <div className="flex flex-wrap gap-1 border-b border-slate-200 dark:border-slate-800" role="tablist">
      {tabs.map((tab) => <button key={tab.value} role="tab" aria-selected={filter.status === tab.value}
        onClick={() => setFilter((current) => ({ ...current, status: tab.value, page: 1 }))}
        className={`-mb-px border-b-2 px-3 py-2 text-sm transition-colors ${filter.status === tab.value
          ? 'border-blue-600 font-medium text-blue-700 dark:text-blue-400'
          : 'border-transparent text-slate-500 hover:text-slate-700 dark:hover:text-slate-300'}`}>
        {tab.label}
      </button>)}
    </div>

    {queue.isPending && <div className="grid gap-4 sm:grid-cols-2">
      {[0, 1, 2, 3].map((index) => <div key={index} className="h-56 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />)}
    </div>}

    {queue.isError && <p role="alert" className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700 dark:border-red-900 dark:bg-red-950">
      The review queue could not be loaded. {queue.error.message}
    </p>}

    {queue.data && queue.data.items.length === 0 && <EmptyState status={filter.status ?? 'Pending'} />}

    {queue.data && queue.data.items.length > 0 && <div className="grid gap-4 sm:grid-cols-2">
      {queue.data.items.map((device) => <DiscoveryCard key={device.id} device={device}
        confirming={confirmingReject === device.id}
        rejecting={reject.isPending && reject.variables === device.id}
        onApprove={() => setApproving(device)}
        onReject={() => confirmingReject === device.id ? reject.mutate(device.id) : setConfirmingReject(device.id)}
        onCancelReject={() => setConfirmingReject(null)} />)}
    </div>}

    {total > pageSize && <div className="flex items-center justify-between text-sm text-slate-500">
      <span className="tabular-nums">{(page - 1) * pageSize + 1}–{Math.min(page * pageSize, total)} of {total}</span>
      <div className="flex gap-2">
        <Button variant="secondary" disabled={page <= 1} onClick={() => setFilter((current) => ({ ...current, page: page - 1 }))} aria-label="Previous page"><ChevronLeft size={18} /></Button>
        <Button variant="secondary" disabled={page >= lastPage} onClick={() => setFilter((current) => ({ ...current, page: page + 1 }))} aria-label="Next page"><ChevronRight size={18} /></Button>
      </div>
    </div>}

    {approving && <DiscoveryApproveDialog device={approving} onClose={() => setApproving(null)}
      onApproved={(device) => {
        setApproving(null)
        void queryClient.invalidateQueries({ queryKey: ['discovered-devices'] })
        toast.success(`${device.suggestedName} approved`)
      }} />}
  </div>
}

function DiscoveryCard({ device, confirming, rejecting, onApprove, onReject, onCancelReject }: {
  device: DiscoveredDevice
  confirming: boolean
  rejecting: boolean
  onApprove: () => void
  onReject: () => void
  onCancelReject: () => void
}) {
  const settled = device.status === 'Approved' || device.status === 'Rejected' || device.status === 'Matched'
  return <article className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
    <div className="flex items-start justify-between gap-3">
      <div className="min-w-0">
        <h2 className="truncate font-semibold">{device.suggestedName}</h2>
        <p className="mt-0.5 flex flex-wrap items-center gap-x-1.5 text-[13px] text-slate-500">
          <span>{device.address}{device.hostname && ` · ${device.hostname}`}</span>
          {/* Where the name came from. A PTR record is what the network's administrator published;
              mDNS and NetBIOS names are whatever the device says about itself, and somebody
              approving one into the CMDB should be able to see which they are trusting. */}
          {device.hostname && device.hostnameSource && <span
            className="rounded bg-slate-100 px-1.5 py-0.5 text-[11px] font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">
            {hostnameSourceLabel(device.hostnameSource)}
          </span>}
        </p>
      </div>
      <span className={`shrink-0 rounded-md px-2 py-0.5 text-xs font-medium ${statusTones[device.status]}`}>{device.status}</span>
    </div>

    <dl className="mt-4 space-y-1.5 text-[13px]">
      <Row label="Suggested type" value={ciTypeLabel(device.suggestedType)} />
      {device.snmp?.sysDescription && <Row label="Reports itself as" value={device.snmp.sysDescription} />}
      {device.snmp?.sysObjectId && <Row label="Object ID" value={device.snmp.sysObjectId} mono />}
      <Row label="Open ports" value={device.openPorts.length > 0
        ? device.openPorts.join(', ')
        // A TCP fingerprint cannot see a UDP service, and reading an empty list as "serves nothing" is
        // wrong about every SNMP agent on the estate. WP-4.1's walk found exactly this.
        : device.respondedToPing ? 'None answered (ICMP only)' : 'None answered'} />
      <Row label="Seen" value={`${device.sightingCount} time${device.sightingCount === 1 ? '' : 's'}, last ${new Date(device.lastSeenAt).toLocaleString()}`} />
      <Row label="Found by" value={device.scanProfileName} />
    </dl>

    {device.neighbours.length > 0 && <div className="mt-4 rounded-lg bg-slate-50 p-3 dark:bg-slate-800/50">
      <p className="flex items-center gap-1.5 text-[13px] font-medium text-slate-600 dark:text-slate-300"><Network size={16} />Neighbours</p>
      <ul className="mt-1.5 space-y-1 text-xs text-slate-500">
        {device.neighbours.map((neighbour, index) => <li key={index}>
          {neighbour.localPort ?? '?'} → {neighbour.remoteSystemName ?? 'unnamed'} ({neighbour.protocol.toUpperCase()})
        </li>)}
      </ul>
    </div>}

    {device.matchRule !== 'None' && <p className="mt-4 text-[13px] text-slate-600 dark:text-slate-300">
      {discoveryMatchRuleLabel(device.matchRule)}
      {device.ciId && device.ciName && <> — <Link to={`/assets/${device.ciId}`} className="text-blue-600 hover:underline dark:text-blue-400">{device.ciName}</Link></>}
    </p>}

    {device.contenders.length > 0 && <div className="mt-2 rounded-lg border border-amber-200 bg-amber-50 p-3 text-[13px] text-amber-800 dark:border-amber-900 dark:bg-amber-950/40 dark:text-amber-300">
      Two CIs claim this device. Approving asks which one it is.
      <ul className="mt-1.5 space-y-0.5">
        {device.contenders.map((contender) => <li key={contender.ciId}>
          <Link to={`/assets/${contender.ciId}`} className="hover:underline">{contender.name}</Link> ({ciTypeLabel(contender.type)})
        </li>)}
      </ul>
    </div>}

    {device.reviewNote && <p className="mt-3 text-[13px] italic text-slate-500">“{device.reviewNote}”{device.reviewedBy && ` — ${device.reviewedBy}`}</p>}

    {!settled && <div className="mt-5 flex gap-2">
      <Button onClick={onApprove}>Approve</Button>
      {confirming
        ? <>
            <Button variant="secondary" onClick={onReject} disabled={rejecting}>{rejecting ? 'Ignoring…' : 'Confirm ignore'}</Button>
            <Button variant="secondary" onClick={onCancelReject} disabled={rejecting}>Cancel</Button>
          </>
        : <Button variant="secondary" onClick={onReject}>Ignore</Button>}
    </div>}
  </article>
}

function Row({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return <div className="flex gap-2">
    <dt className="w-32 shrink-0 text-slate-500">{label}</dt>
    <dd className={`min-w-0 flex-1 break-words text-slate-700 dark:text-slate-300 ${mono ? 'font-mono text-xs' : ''}`}>{value}</dd>
  </div>
}

function EmptyState({ status }: { status: DiscoveredDeviceStatus | 'all' }) {
  const messages: Record<string, string> = {
    Pending: 'Every device the last scan found is already in the CMDB. New hardware appears here the first time a scan sees it.',
    Matched: 'No scan has placed a device against an existing CI yet.',
    Approved: 'Nothing has been approved into the CMDB from a scan yet.',
    Rejected: 'Nothing is on the ignore list. Rejecting a device here keeps it off this queue on every later scan.',
    all: 'No scan has reported anything yet. Check that a scan profile is enabled and that the discovery service is running.',
  }
  return <div className="rounded-xl border border-slate-200 bg-white px-6 py-16 text-center dark:border-slate-800 dark:bg-slate-900">
    <div className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/10">
      <Radar size={24} />
    </div>
    <p className="mx-auto mt-4 max-w-md text-sm text-slate-500">{messages[status]}</p>
  </div>
}
