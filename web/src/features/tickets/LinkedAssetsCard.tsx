import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Boxes, Link2, Link2Off, Plus, Search, X } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { assetsApi, ciTypeLabel } from '../../api/assets'
import { contractStatusLabel, contractStatusTone } from '../../api/contracts'
import { helpdeskApi, type TicketCiLink } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { formatDateOnly } from '../../lib/utils'
import { ciLifecycleLabel, ciLifecycleTone } from '../assets/lifecycle'
import { formatLocal } from './ticketUi'

/**
 * The warranty in a sentence. `formatDateOnly` rather than `new Date(...)`, because a `DateOnly`
 * parsed as an instant is UTC midnight and renders as the previous day west of Greenwich.
 */
function warrantyLine(link: TicketCiLink) {
  if (link.warrantyExpiresAt === null) return null
  const on = formatDateOnly(link.warrantyExpiresAt)
  const days = link.warrantyDaysRemaining
  if (days === null) return `Warranty ends ${on}`
  if (days < 0) return `Warranty expired ${Math.abs(days)} day${Math.abs(days) === 1 ? '' : 's'} ago, on ${on}`
  if (days === 0) return `Warranty expires today, ${on}`
  return `Warranty expires in ${days} day${days === 1 ? '' : 's'}, on ${on}`
}

/** The CIs a ticket is about, and the picker that links another one. */
export function LinkedAssetsCard({ ticketId }: { ticketId: string }) {
  const queryClient = useQueryClient()
  const [picking, setPicking] = useState(false)
  const [confirmingId, setConfirmingId] = useState<string | null>(null)
  const links = useQuery({ queryKey: ['tickets', ticketId, 'cis'], queryFn: () => helpdeskApi.getTicketCis(ticketId), enabled: Boolean(ticketId) })

  // Both worlds have to reflect the change: the ticket's own card, and every asset page whose ticket
  // list is keyed by a CI id.
  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ['tickets', ticketId, 'cis'] })
    await queryClient.invalidateQueries({ queryKey: ['tickets'] })
  }

  const unlink = useMutation({
    mutationFn: (ciId: string) => helpdeskApi.unlinkTicketCi(ticketId, ciId),
    onSuccess: async () => { setConfirmingId(null); await refresh(); toast.success('Asset unlinked') },
  })

  const items = links.data ?? []

  return <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
    <div className="flex items-center gap-3 border-b border-slate-200 p-5 dark:border-slate-800">
      <div><h2 className="font-semibold">Linked assets</h2><p className="mt-1 text-sm text-slate-500">The configuration items this ticket is about.</p></div>
      <Button variant="secondary" className="ml-auto h-9 shrink-0 text-[13px]" onClick={() => setPicking(true)}><Plus size={16} />Link asset</Button>
    </div>

    {links.isLoading ? <div aria-label="Loading linked assets" className="space-y-2 p-5">{Array.from({ length: 2 }, (_, index) => <div key={index} className="h-16 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />)}</div>
      : links.isError ? <div role="alert" className="p-5 text-sm text-red-600">Linked assets could not be loaded.</div>
      : items.length === 0 ? <div className="grid place-items-center p-8 text-center"><div>
          <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><Boxes /></span>
          <p className="mt-3 text-sm text-slate-500">No assets linked yet. Link the CI this ticket is about so both sides tell the same story.</p>
          <Button className="mt-4" variant="secondary" onClick={() => setPicking(true)}><Link2 size={16} />Link an asset</Button>
        </div></div>
      : <ul className="divide-y divide-slate-200 dark:divide-slate-800">
          {items.map((link) => <li key={link.id} className="flex flex-wrap items-center gap-3 p-4">
            <div className="min-w-0 flex-1">
              <div className="flex flex-wrap items-center gap-2">
                <Link to={`/assets/${link.ciId}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{link.ciName}</Link>
                <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${ciLifecycleTone(link.lifecycleState)}`}>{ciLifecycleLabel(link.lifecycleState)}</span>
                {link.warrantyStatus && <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${contractStatusTone(link.warrantyStatus)}`}>
                  Warranty {contractStatusLabel(link.warrantyStatus).toLowerCase()}
                </span>}
              </div>
              <p className="mt-1 text-[13px] text-slate-500">
                {ciTypeLabel(link.ciType)}
                {link.assetTag && <> · <span className="font-mono">{link.assetTag}</span></>}
                {link.ownerName && <> · {link.ownerName}</>}
                {link.siteName && <> · {link.siteName}</>}
                {link.departmentName && <> · {link.departmentName}</>}
              </p>
              {/* The warranty date in words, because "expiring soon" alone is a pill nobody can plan around. */}
              {link.warrantyExpiresAt && <p className="mt-1 text-xs text-slate-500">{warrantyLine(link)}{link.contractName && <> · Covered by {link.contractName}</>}</p>}
              {link.openRelatedTickets.length > 0 && <div className="mt-2 rounded-lg border border-slate-200 bg-slate-50 p-2 dark:border-slate-800 dark:bg-slate-800/40">
                <p className="px-1 text-xs font-medium text-slate-500">
                  Also open on this asset ({link.openRelatedTickets.length})
                </p>
                <ul className="mt-1">
                  {link.openRelatedTickets.map((related) => <li key={related.ticketId} className="px-1 py-0.5 text-[13px]">
                    <Link to={`/tickets/${related.ticketId}`} className="font-mono text-slate-500 hover:text-blue-600">{related.number}</Link>
                    {' '}<span className="text-slate-600 dark:text-slate-300">{related.title}</span>
                    <span className="text-slate-500"> · {related.status} · {related.priority}</span>
                  </li>)}
                </ul>
              </div>}
              <p className="mt-1 text-xs text-slate-500">Linked by {link.linkedByName} · {formatLocal(link.linkedAt)}</p>
            </div>
            {confirmingId === link.ciId
              ? <Button variant="secondary" className="h-9 text-[13px] text-red-600" disabled={unlink.isPending} onClick={() => unlink.mutate(link.ciId)}>Confirm unlink</Button>
              : <Button variant="ghost" className="h-9 px-2 text-[13px] text-red-600" onClick={() => setConfirmingId(link.ciId)}><Link2Off size={16} />Unlink</Button>}
          </li>)}
        </ul>}

    {unlink.error && <p role="alert" className="border-t border-slate-200 p-4 text-sm text-red-600 dark:border-slate-800">{unlink.error.message}</p>}
    {picking && <AssetPicker ticketId={ticketId} linked={items} onLinked={refresh} onClose={() => setPicking(false)} />}
  </section>
}

function AssetPicker({ ticketId, linked, onLinked, onClose }: { ticketId: string; linked: TicketCiLink[]; onLinked: () => Promise<void>; onClose: () => void }) {
  const [search, setSearch] = useState('')
  const [term, setTerm] = useState('')
  useEffect(() => {
    const timer = window.setTimeout(() => setTerm(search), 200)
    return () => window.clearTimeout(timer)
  }, [search])

  const cis = useQuery({ queryKey: ['cis', { search: term, pageSize: 10 }], queryFn: () => assetsApi.listCis({ search: term, pageSize: 10 }), placeholderData: keepPreviousData })
  const link = useMutation({
    mutationFn: (ciId: string) => helpdeskApi.linkTicketCi(ticketId, ciId),
    onSuccess: async (created) => { await onLinked(); toast.success(`${created.ciName} linked`) },
  })
  const alreadyLinked = new Set(linked.map((item) => item.ciId))

  return <div className="fixed inset-0 z-50 grid place-items-center bg-slate-950/50 p-4" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
    <section role="dialog" aria-modal="true" aria-labelledby="link-asset-title" className="max-h-[90vh] w-full max-w-xl overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <div className="mb-4 flex items-start"><div><h2 id="link-asset-title" className="text-lg font-semibold">Link an asset</h2><p className="mt-1 text-sm text-slate-500">Search the CMDB by name, asset tag, or serial number.</p></div><Button variant="ghost" className="ml-auto size-9 p-0" aria-label="Close" onClick={onClose}><X size={19} /></Button></div>
      <label className="flex h-10 items-center gap-2 rounded-lg border border-slate-200 px-3 text-slate-500 dark:border-slate-700">
        <Search size={17} /><span className="sr-only">Search configuration items</span>
        <input autoFocus value={search} onChange={(event) => setSearch(event.target.value)} className="w-full bg-transparent text-sm text-slate-900 outline-none dark:text-slate-100" placeholder="Search names, asset tags, and serials…" />
      </label>
      {link.error && <p role="alert" className="mt-3 text-sm text-red-600">{link.error.message}</p>}
      <ul className="mt-4 divide-y divide-slate-200 dark:divide-slate-800">
        {cis.isLoading && <li className="py-6 text-center text-sm text-slate-500">Searching…</li>}
        {!cis.isLoading && (cis.data?.items.length ?? 0) === 0 && <li className="py-6 text-center text-sm text-slate-500">No configuration items match.</li>}
        {cis.data?.items.map((ci) => <li key={ci.id} className="flex items-center gap-3 py-3">
          <div className="min-w-0">
            <p className="text-sm font-medium">{ci.name}</p>
            <p className="mt-0.5 text-[13px] text-slate-500">{ciTypeLabel(ci.type)}{ci.assetTag && <> · <span className="font-mono">{ci.assetTag}</span></>} · {ciLifecycleLabel(ci.lifecycleState)}</p>
          </div>
          <Button variant="secondary" className="ml-auto h-9 shrink-0 text-[13px]" disabled={alreadyLinked.has(ci.id) || link.isPending} onClick={() => link.mutate(ci.id)}>
            {alreadyLinked.has(ci.id) ? 'Linked' : <><Link2 size={15} />Link</>}
          </Button>
        </li>)}
      </ul>
    </section>
  </div>
}
