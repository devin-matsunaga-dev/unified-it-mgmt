import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronLeft, Search, UserRound } from 'lucide-react'
import { useEffect, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { toast } from 'sonner'
import { assetsApi } from '../../api/assets'
import { directoryApi } from '../../api/directory'
import { Button } from '../../components/ui/Button'
import { FieldActionBar } from '../../layout/FieldShell'
import { cn } from '../../lib/utils'

/**
 * Handing an asset over, taking it back, passing it on, or moving it. One screen for all four,
 * because the API is one call: `PUT /api/cis/{id}/assignment` **replaces** owner, department and
 * site with exactly what it is sent. A screen per action would each have to remember to resend the
 * two fields it was not changing, and the first one that forgot would quietly clear a site.
 *
 * So every control is on screen, prefilled from what the asset carries now, and Save sends all three
 * together. What that adds up to — a check-out, a check-in, a transfer or a move — is classified by
 * the server from what actually changed; the verb on the button is only a label, never the decision.
 */
export function FieldAssignPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const ci = useQuery({ queryKey: ['ci', id], queryFn: () => assetsApi.getCi(id), enabled: Boolean(id) })
  const users = useQuery({ queryKey: ['directory', 'users'], queryFn: directoryApi.listUsers })
  const departments = useQuery({ queryKey: ['directory', 'departments'], queryFn: directoryApi.listDepartments })
  const sites = useQuery({ queryKey: ['directory', 'sites'], queryFn: directoryApi.listSites })

  const [ownerUserId, setOwnerUserId] = useState<string | null>(null)
  const [departmentId, setDepartmentId] = useState<string | null>(null)
  const [siteId, setSiteId] = useState<string | null>(null)
  const [note, setNote] = useState('')
  const [search, setSearch] = useState('')

  // Prefilled from the asset once it lands, so an untouched form is a no-op rather than a wipe.
  useEffect(() => {
    if (!ci.data) return
    setOwnerUserId(ci.data.ownership.ownerUserId)
    setDepartmentId(ci.data.ownership.departmentId)
    setSiteId(ci.data.ownership.siteId)
  }, [ci.data])

  const save = useMutation({
    mutationFn: () => assetsApi.assignCi(id, { ownerUserId, departmentId, siteId, note: note.trim() || null }),
    onSuccess: async (updated) => {
      await queryClient.invalidateQueries({ queryKey: ['ci', id] })
      toast.success(`${updated.name} is now with ${updated.ownership.ownerName ?? 'nobody'}`)
      navigate(`/field/ci/${id}`, { replace: true })
    },
    onError: (error: Error) => toast.error(error.message),
  })

  if (ci.isLoading || !ci.data) {
    return <div aria-label="Loading" className="space-y-3">
      <div className="h-24 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
      <div className="h-48 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
    </div>
  }

  const item = ci.data
  const current = item.ownership
  const changed = ownerUserId !== current.ownerUserId
    || departmentId !== current.departmentId
    || siteId !== current.siteId

  // The whole directory arrives in one call with no server-side search, so the filter is here.
  const matches = (users.data ?? []).filter((user) => {
    const needle = search.trim().toLowerCase()
    if (!needle) return true
    return user.displayName.toLowerCase().includes(needle) || user.username.toLowerCase().includes(needle)
  })

  return <>
    <Link to={`/field/ci/${id}`} className="inline-flex h-11 items-center gap-1 text-[15px] font-medium text-blue-600">
      <ChevronLeft size={18} />Back
    </Link>
    <h1 className="mt-1 text-[22px] font-bold leading-tight">Hand over or move</h1>
    <p className="mt-1 truncate text-[15px] text-slate-500">{item.name}</p>

    <section className="mt-5 rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="text-base font-semibold">Holder</h2>
      <p className="mt-1 text-[13px] text-slate-500">Now with {current.ownerName ?? 'nobody'}.</p>

      <button
        type="button"
        aria-pressed={ownerUserId === null}
        onClick={() => setOwnerUserId(null)}
        className={cn(
          'mt-3 flex h-12 w-full items-center gap-2 rounded-lg border px-3 text-[15px] font-medium focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600',
          ownerUserId === null
            ? 'border-blue-600 bg-blue-50 text-blue-700 dark:bg-blue-950 dark:text-blue-300'
            : 'border-slate-200 bg-white text-slate-600 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300',
        )}
      ><UserRound size={17} />Nobody — back into stock</button>

      <div className="relative mt-3">
        <Search size={17} className="absolute left-3 top-1/2 -translate-y-1/2 text-slate-400" />
        <input
          aria-label="Search people"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
          autoComplete="off"
          // 16px, or iOS Safari zooms the page on focus.
          className="h-12 w-full rounded-lg border border-slate-200 bg-white pl-10 pr-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
        />
      </div>

      <ul className="mt-2 max-h-64 space-y-1 overflow-y-auto">
        {matches.map((user) => <li key={user.id}>
          <button
            type="button"
            aria-pressed={ownerUserId === user.id}
            onClick={() => setOwnerUserId(user.id)}
            className={cn(
              'flex min-h-12 w-full items-center rounded-lg border px-3 py-2 text-left text-[15px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600',
              ownerUserId === user.id
                ? 'border-blue-600 bg-blue-50 text-blue-700 dark:bg-blue-950 dark:text-blue-300'
                : 'border-slate-200 bg-white dark:border-slate-700 dark:bg-slate-900',
            )}
          >
            <span className="min-w-0">
              <span className="block truncate font-medium">{user.displayName}</span>
              <span className="block truncate text-[13px] text-slate-500">{user.departmentName} · {user.siteName}</span>
            </span>
          </button>
        </li>)}
        {matches.length === 0 && <li className="px-1 py-3 text-[15px] text-slate-500">Nobody matches that.</li>}
      </ul>
    </section>

    <section className="mt-3 rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="text-base font-semibold">Where it lives</h2>
      <label htmlFor="field-assign-site" className="mt-3 block text-[13px] font-medium text-slate-500">Site</label>
      <select
        id="field-assign-site"
        value={siteId ?? ''}
        onChange={(event) => setSiteId(event.target.value || null)}
        className="mt-1.5 h-12 w-full rounded-lg border border-slate-200 bg-white px-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
      >
        <option value="">Nowhere recorded</option>
        {sites.data?.map((site) => <option key={site.id} value={site.id}>{site.name}</option>)}
      </select>

      <label htmlFor="field-assign-department" className="mt-4 block text-[13px] font-medium text-slate-500">Department</label>
      <select
        id="field-assign-department"
        value={departmentId ?? ''}
        onChange={(event) => setDepartmentId(event.target.value || null)}
        className="mt-1.5 h-12 w-full rounded-lg border border-slate-200 bg-white px-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
      >
        <option value="">None recorded</option>
        {departments.data?.map((department) => <option key={department.id} value={department.id}>{department.name}</option>)}
      </select>

      <label htmlFor="field-assign-note" className="mt-4 block text-[13px] font-medium text-slate-500">Note (optional)</label>
      <input
        id="field-assign-note"
        value={note}
        onChange={(event) => setNote(event.target.value)}
        className="mt-1.5 h-12 w-full rounded-lg border border-slate-200 bg-white px-3 text-base focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:border-slate-700 dark:bg-slate-900"
      />
    </section>

    <FieldActionBar>
      <Button className="h-12 w-full text-[15px]" disabled={!changed || save.isPending} onClick={() => save.mutate()}>
        {save.isPending ? 'Saving…' : actionLabel(current.ownerUserId, ownerUserId)}
      </Button>
    </FieldActionBar>
  </>
}

/**
 * The verb for the button only. The server classifies what actually happened from what changed —
 * this must never be sent, or the phone would hold a second copy of that rule.
 */
export function actionLabel(from: string | null, to: string | null): string {
  if (from === null && to !== null) return 'Check out'
  if (from !== null && to === null) return 'Check in'
  if (from !== null && to !== null && from !== to) return 'Transfer'
  return 'Move it'
}
