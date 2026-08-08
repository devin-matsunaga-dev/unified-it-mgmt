import { useMutation, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Pencil, ShieldCheck, SlidersHorizontal, Ticket } from 'lucide-react'
import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { toast } from 'sonner'
import { assetsApi, ciTypeLabel } from '../../api/assets'
import { contractStatusLabel, contractStatusTone, describeDaysRemaining } from '../../api/contracts'
import { helpdeskApi } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { PriorityPill, StatusPill, formatLocal } from '../tickets/ticketUi'
import { CiCoverageDialog } from './CiCoverageDialog'
import { CiFormDialog, type CiFormSubmit } from './CiFormDialog'
import { CiLifecycleDrawer } from './CiLifecycleDrawer'
import { CiRelationsGraph } from './CiRelationsGraph'
import { ciLifecycleLabel, ciLifecycleTone, describeAssignment } from './lifecycle'

/** The asset 360° page: what the CI is, what it depends on, and every ticket ever raised about it. */
export function CiDetailPage() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState(false)
  const [lifecycleOpen, setLifecycleOpen] = useState(false)
  const [coverageOpen, setCoverageOpen] = useState(false)

  const ci = useQuery({ queryKey: ['cis', id], queryFn: () => assetsApi.getCi(id), enabled: Boolean(id) })
  const schemas = useQuery({ queryKey: ['ci-type-schemas'], queryFn: assetsApi.listTypeSchemas, staleTime: 0, refetchOnMount: 'always' })
  const lifecycleStates = useQuery({ queryKey: ['ci-lifecycle-states'], queryFn: assetsApi.listLifecycleStates })
  const [tickets, history, assignments] = useQueries({ queries: [
    { queryKey: ['tickets', { ciId: id }], queryFn: () => helpdeskApi.listTickets({ ciId: id }), enabled: Boolean(id) },
    { queryKey: ['ci-lifecycle-history', id], queryFn: () => assetsApi.getLifecycleHistory(id), enabled: Boolean(id) },
    { queryKey: ['ci-assignments', id], queryFn: () => assetsApi.getAssignments(id), enabled: Boolean(id) },
  ] })

  const save = useMutation({
    mutationFn: (input: CiFormSubmit) => assetsApi.updateCi(id, { name: input.name, assetTag: input.assetTag, serialNumber: input.serialNumber, description: input.description, isActive: input.isActive, attributes: input.attributes, customFields: input.customFields }),
    onSuccess: async (updated) => { await queryClient.invalidateQueries({ queryKey: ['cis'] }); setEditing(false); toast.success(`${updated.name} updated`) },
  })

  if (ci.isLoading) return <DetailSkeleton />
  if (ci.isError || !ci.data) return <div role="alert" className="rounded-xl border border-red-200 bg-white p-8 text-center dark:border-red-900 dark:bg-slate-900">
    <h1 className="font-semibold">Configuration item could not be loaded</h1>
    <p className="mt-2 text-sm text-slate-500">{ci.error instanceof Error ? ci.error.message : 'The CI may not exist.'}</p>
    <Button className="mt-4" variant="secondary" onClick={() => void ci.refetch()}>Try again</Button>
  </div>

  const item = ci.data
  const schema = schemas.data?.find((entry) => entry.type === item.type)
  const ticketItems = tickets.data?.items ?? []

  return <div className="space-y-6">
    <Link to="/assets" className="inline-flex items-center gap-2 text-sm text-slate-500 hover:text-blue-600"><ArrowLeft size={17} />Back to assets</Link>

    <header className="flex flex-col gap-4 xl:flex-row xl:items-start">
      <div className="min-w-0">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-sm text-slate-500">{ciTypeLabel(item.type)}</span>
          <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${ciLifecycleTone(item.lifecycleState)}`}>{ciLifecycleLabel(item.lifecycleState)}</span>
          {!item.isActive && <span className="rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600 dark:bg-slate-500/15 dark:text-slate-400">Inactive</span>}
        </div>
        <h1 className="mt-2 text-[28px] font-bold leading-tight">{item.name}</h1>
        <p className="mt-2 text-sm text-slate-500">
          {item.assetTag ? <>Asset tag <span className="font-mono">{item.assetTag}</span></> : 'No asset tag'}
          {item.serialNumber && <> · serial <span className="font-mono">{item.serialNumber}</span></>}
          {' '}· registered {formatLocal(item.createdAt)}
        </p>
      </div>
      <div className="flex gap-2 xl:ml-auto">
        <Button variant="secondary" onClick={() => setEditing(true)}><Pencil size={16} />Edit</Button>
        <Button variant="secondary" onClick={() => setLifecycleOpen(true)}><SlidersHorizontal size={16} />Lifecycle</Button>
      </div>
    </header>

    <div className="grid gap-6 xl:grid-cols-[minmax(0,2fr)_minmax(300px,1fr)]">
      <div className="space-y-6">
        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
          <h2 className="font-semibold">Details</h2>
          {item.description && <p className="mt-3 whitespace-pre-wrap text-sm leading-6 text-slate-600 dark:text-slate-300">{item.description}</p>}
          <dl className="mt-4 space-y-3 text-sm">
            {(schema?.attributes ?? []).map((attribute) => <Detail key={attribute.key} label={attribute.label} value={item.attributes[attribute.key] || '—'} />)}
          </dl>
          {item.customFields.length > 0 && <>
            <h3 className="mt-5 text-[13px] font-medium text-slate-500">Custom fields</h3>
            <dl className="mt-3 space-y-3 text-sm">{item.customFields.map((field) => <Detail key={field.fieldId} label={field.label} value={field.value || '—'} />)}</dl>
          </>}
        </section>

        <CiRelationsGraph ci={item} />

        <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
          <div className="flex items-center gap-3 border-b border-slate-200 p-5 dark:border-slate-800">
            <div><h2 className="font-semibold">Ticket history</h2><p className="mt-1 text-sm text-slate-500">Every ticket ever linked to this asset.</p></div>
            <span className="ml-auto text-[13px] text-slate-500">{tickets.data?.total ?? 0} tickets</span>
          </div>
          {tickets.isLoading ? <div aria-label="Loading tickets" className="space-y-2 p-5">{Array.from({ length: 3 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />)}</div>
            : tickets.isError ? <div role="alert" className="p-5 text-sm text-red-600">Tickets could not be loaded.</div>
            : ticketItems.length === 0 ? <div className="grid place-items-center p-8 text-center"><div>
                <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><Ticket /></span>
                <p className="mt-3 text-sm text-slate-500">No tickets link to this asset yet. Link it from a ticket's "Linked assets" card.</p>
              </div></div>
            : <ul className="divide-y divide-slate-200 dark:divide-slate-800">
                {ticketItems.map((ticket) => <li key={ticket.id} className="flex flex-wrap items-center gap-3 p-4">
                  <span className="font-mono text-xs text-slate-500">#{ticket.number}</span>
                  <Link to={`/tickets/${ticket.id}`} className="min-w-0 flex-1 truncate font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{ticket.title}</Link>
                  <StatusPill status={ticket.status} />
                  <PriorityPill priority={ticket.priority} />
                  <span className="text-xs text-slate-500">{formatLocal(ticket.createdAt)}</span>
                </li>)}
              </ul>}
        </section>
      </div>

      <aside className="space-y-6">
        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
          <h2 className="font-semibold">Ownership</h2>
          <dl className="mt-4 space-y-3 text-sm">
            <div className="flex gap-4">
              <dt className="text-slate-500">Owner</dt>
              <dd className="ml-auto max-w-[65%] break-words text-right font-medium">
                {item.ownership.ownerUserId
                  ? <Link to={`/people/${item.ownership.ownerUserId}`} className="text-blue-600 hover:underline">{item.ownership.ownerName ?? 'Unnamed user'}</Link>
                  : 'Unassigned'}
              </dd>
            </div>
            <Detail label="Department" value={item.ownership.departmentName ?? '—'} />
            <Detail label="Location" value={item.ownership.siteName ?? '—'} />
            <Detail label="Assigned" value={item.ownership.assignedAt ? formatLocal(item.ownership.assignedAt) : '—'} />
            <Detail label="Updated" value={formatLocal(item.updatedAt)} />
          </dl>
        </section>

        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
          <div className="flex items-center gap-3">
            <h2 className="font-semibold">Warranty &amp; contract</h2>
            <Button variant="ghost" className="ml-auto h-8 px-2 text-[13px]" onClick={() => setCoverageOpen(true)}><ShieldCheck size={15} />Edit</Button>
          </div>
          <dl className="mt-3 space-y-3 text-sm">
            <div className="flex gap-4">
              <dt className="text-slate-500">Contract</dt>
              <dd className="ml-auto max-w-[65%] break-words text-right font-medium">
                {item.coverage.contractId
                  ? <Link to={`/contracts/${item.coverage.contractId}`} className="text-blue-600 hover:underline">{item.coverage.contractNumber} — {item.coverage.contractName}</Link>
                  : 'Not covered'}
              </dd>
            </div>
            {item.coverage.vendorName && <Detail label="Vendor" value={item.coverage.vendorName} />}
            {item.coverage.contractEndDate && <Detail label="Contract ends" value={item.coverage.contractEndDate} />}
            <Detail label="Purchased" value={item.coverage.purchaseDate ?? '—'} />
            <div className="flex gap-4">
              <dt className="text-slate-500">Warranty ends</dt>
              <dd className="ml-auto flex max-w-[65%] flex-wrap items-center justify-end gap-2 text-right font-medium">
                {item.coverage.warrantyExpiresAt ? <>
                  <span>{item.coverage.warrantyExpiresAt}</span>
                  <span className={`rounded-md px-2 py-0.5 text-xs font-medium ${contractStatusTone(item.coverage.warrantyStatus ?? 'Active')}`}>
                    {item.coverage.warrantyStatus === 'Expired'
                      ? contractStatusLabel('Expired')
                      : describeDaysRemaining(item.coverage.warrantyDaysRemaining ?? 0)}
                  </span>
                </> : '—'}
              </dd>
            </div>
          </dl>
        </section>

        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
          <h2 className="font-semibold">Lifecycle history</h2>
          <ol className="mt-4 space-y-3 text-sm">
            {(history.data ?? []).length === 0 && <li className="text-sm text-slate-500">Registered as {ciLifecycleLabel(item.lifecycleState)}; no transitions yet.</li>}
            {(history.data ?? []).map((entry) => <li key={entry.id}>
              <p className="font-medium">{ciLifecycleLabel(entry.fromState)} → {ciLifecycleLabel(entry.toState)}</p>
              {entry.note && <p className="mt-1 text-slate-600 dark:text-slate-300">{entry.note}</p>}
              <p className="mt-1 text-xs text-slate-500">{entry.actorId} · {formatLocal(entry.occurredAt)}</p>
            </li>)}
          </ol>
        </section>

        <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
          <h2 className="font-semibold">Check-in / out log</h2>
          <ol className="mt-4 space-y-3 text-sm">
            {(assignments.data ?? []).length === 0 && <li className="text-sm text-slate-500">Nobody has held this asset yet.</li>}
            {(assignments.data ?? []).map((entry) => <li key={entry.id}>
              <p className="font-medium">{describeAssignment(entry)}</p>
              <p className="mt-1 text-xs text-slate-500">{entry.actorId} · {formatLocal(entry.occurredAt)}</p>
            </li>)}
          </ol>
        </section>
      </aside>
    </div>

    <CiFormDialog open={editing} ci={item} schemas={schemas.data ?? []} pending={save.isPending}
      error={save.error instanceof Error ? save.error.message : undefined}
      onClose={() => { if (!save.isPending) { setEditing(false); save.reset() } }}
      onSubmit={async (input) => { await save.mutateAsync(input) }} />

    <CiLifecycleDrawer ci={lifecycleOpen ? item : null} states={lifecycleStates.data ?? []} onClose={() => setLifecycleOpen(false)} />

    <CiCoverageDialog ci={coverageOpen ? item : null} onClose={() => setCoverageOpen(false)} />
  </div>
}

function Detail({ label, value }: { label: string; value: string }) {
  return <div className="flex gap-4"><dt className="text-slate-500">{label}</dt><dd className="ml-auto max-w-[65%] break-words text-right font-medium">{value}</dd></div>
}

function DetailSkeleton() {
  return <div aria-label="Loading configuration item" className="space-y-6">
    <div className="h-24 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
    <div className="grid gap-6 xl:grid-cols-[2fr_1fr]">
      <div className="h-96 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
      <div className="h-72 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
    </div>
  </div>
}
