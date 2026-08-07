import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowRight, History, UserRound, X } from 'lucide-react'
import { useEffect, useState } from 'react'
import { toast } from 'sonner'
import { assetsApi, type Ci, type CiLifecycleState, type CiLifecycleStateInfo } from '../../api/assets'
import { directoryApi } from '../../api/directory'
import { Button } from '../../components/ui/Button'
import { allowedTargets, ciAssignmentLabel, ciLifecycleLabel, ciLifecycleTone, describeAssignment } from './lifecycle'

/**
 * Detail-peek drawer (DESIGN.md §6) for one CI's lifecycle and ownership. The legal next states come
 * from the server's graph, so the browser never has a second copy of the guard.
 */
export function CiLifecycleDrawer({ ci, states, onClose }: {
  ci: Ci | null
  states: CiLifecycleStateInfo[]
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const [note, setNote] = useState('')
  const [ownerUserId, setOwnerUserId] = useState('')
  const [departmentId, setDepartmentId] = useState('')
  const [siteId, setSiteId] = useState('')

  useEffect(() => {
    setNote('')
    setOwnerUserId(ci?.ownership.ownerUserId ?? '')
    setDepartmentId(ci?.ownership.departmentId ?? '')
    setSiteId(ci?.ownership.siteId ?? '')
  }, [ci])

  const users = useQuery({ queryKey: ['directory', 'users'], queryFn: directoryApi.listUsers, enabled: Boolean(ci) })
  const departments = useQuery({ queryKey: ['directory', 'departments'], queryFn: directoryApi.listDepartments, enabled: Boolean(ci) })
  const sites = useQuery({ queryKey: ['directory', 'sites'], queryFn: directoryApi.listSites, enabled: Boolean(ci) })
  const history = useQuery({ queryKey: ['ci-lifecycle-history', ci?.id], queryFn: () => assetsApi.getLifecycleHistory(ci!.id), enabled: Boolean(ci) })
  const assignments = useQuery({ queryKey: ['ci-assignments', ci?.id], queryFn: () => assetsApi.getAssignments(ci!.id), enabled: Boolean(ci) })

  const refresh = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['cis'] }),
      queryClient.invalidateQueries({ queryKey: ['ci-lifecycle-history', ci?.id] }),
      queryClient.invalidateQueries({ queryKey: ['ci-assignments', ci?.id] }),
    ])
  }

  const transition = useMutation({
    mutationFn: (target: CiLifecycleState) => assetsApi.transitionCi(ci!.id, target, note.trim() || null),
    onSuccess: async (updated) => {
      await refresh()
      setNote('')
      toast.success(`${updated.name} is now ${ciLifecycleLabel(updated.lifecycleState).toLowerCase()}`)
    },
    onError: (error: Error) => toast.error(error.message),
  })

  const assign = useMutation({
    mutationFn: () => assetsApi.assignCi(ci!.id, {
      ownerUserId: ownerUserId || null,
      departmentId: departmentId || null,
      siteId: siteId || null,
      note: note.trim() || null,
    }),
    onSuccess: async (updated) => {
      await refresh()
      setNote('')
      toast.success(updated.ownership.ownerName ? `Checked out to ${updated.ownership.ownerName}` : `${updated.name} checked in`)
    },
    onError: (error: Error) => toast.error(error.message),
  })

  if (!ci) return null

  const targets = allowedTargets(states, ci.lifecycleState)
  const frozen = ci.lifecycleState === 'Disposed'
  const busy = transition.isPending || assign.isPending

  // Picking a person prefills their department and site, which is right far more often than not;
  // both stay editable because a laptop can sit at a site its owner does not work from.
  const selectOwner = (id: string) => {
    setOwnerUserId(id)
    const user = users.data?.find((candidate) => candidate.id === id)
    if (user) {
      setDepartmentId(user.departmentId)
      setSiteId(user.siteId)
    }
  }

  return <div className="fixed inset-0 z-20 flex justify-end bg-slate-900/40" role="dialog" aria-modal="true" aria-label={`Lifecycle and ownership for ${ci.name}`}>
    <div className="h-full w-full max-w-[480px] overflow-y-auto border-l border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <div className="flex items-start gap-3">
        <div>
          <h2 className="text-lg font-semibold">{ci.name}</h2>
          <p className="mt-1 text-sm text-slate-500">Lifecycle and ownership</p>
        </div>
        <Button variant="ghost" className="ml-auto size-9 p-0" onClick={onClose} aria-label="Close"><X size={18} /></Button>
      </div>

      <section className="mt-6">
        <h3 className="text-[13px] font-medium text-slate-500">Lifecycle state</h3>
        <p className="mt-2"><span className={`rounded-md px-2 py-0.5 text-xs font-medium ${ciLifecycleTone(ci.lifecycleState)}`}>{ciLifecycleLabel(ci.lifecycleState)}</span></p>
        {targets.length === 0
          ? <p className="mt-3 text-sm text-slate-500">{frozen ? 'A disposed CI is a closed record — it can no longer be moved or edited.' : 'This CI has no further lifecycle states.'}</p>
          : <div className="mt-3 flex flex-wrap gap-2">
              {targets.map((target) => <Button key={target} variant="secondary" disabled={busy} onClick={() => transition.mutate(target)}>
                <ArrowRight size={16} />{ciLifecycleLabel(target)}
              </Button>)}
            </div>}
      </section>

      <section className="mt-6">
        <h3 className="text-[13px] font-medium text-slate-500">Assignment</h3>
        <div className="mt-2 space-y-3">
          <Field label="Owner" htmlFor="ci-owner">
            <select id="ci-owner" className="input h-11" value={ownerUserId} disabled={frozen} onChange={(event) => selectOwner(event.target.value)}>
              <option value="">Unassigned (check in)</option>
              {(users.data ?? []).map((user) => <option key={user.id} value={user.id}>{user.displayName}</option>)}
            </select>
          </Field>
          <Field label="Department" htmlFor="ci-department">
            <select id="ci-department" className="input h-11" value={departmentId} disabled={frozen} onChange={(event) => setDepartmentId(event.target.value)}>
              <option value="">No department</option>
              {(departments.data ?? []).map((department) => <option key={department.id} value={department.id}>{department.name}</option>)}
            </select>
          </Field>
          <Field label="Location" htmlFor="ci-site">
            <select id="ci-site" className="input h-11" value={siteId} disabled={frozen} onChange={(event) => setSiteId(event.target.value)}>
              <option value="">No location</option>
              {(sites.data ?? []).map((site) => <option key={site.id} value={site.id}>{site.name}</option>)}
            </select>
          </Field>
          <Field label="Note" htmlFor="ci-note">
            <input id="ci-note" className="input h-11" value={note} disabled={frozen} placeholder="Why is this changing?" onChange={(event) => setNote(event.target.value)} />
          </Field>
          <Button disabled={busy || frozen} onClick={() => assign.mutate()}><UserRound size={16} />{assign.isPending ? 'Saving…' : 'Save assignment'}</Button>
        </div>
      </section>

      <section className="mt-8">
        <h3 className="flex items-center gap-2 text-[13px] font-medium text-slate-500"><History size={15} />Lifecycle history</h3>
        {(history.data ?? []).length === 0
          ? <p className="mt-2 text-sm text-slate-500">No transitions yet — this CI is still where it was registered.</p>
          : <ol className="mt-2 space-y-2">
              {history.data!.map((entry) => <li key={entry.id} className="border-l-2 border-slate-200 pl-3 text-sm dark:border-slate-700">
                <p className="text-slate-700 dark:text-slate-200">{ciLifecycleLabel(entry.fromState)} → {ciLifecycleLabel(entry.toState)}</p>
                {entry.note && <p className="text-slate-600 dark:text-slate-400">{entry.note}</p>}
                <p className="text-xs text-slate-500">{entry.actorId} · {new Date(entry.occurredAt).toLocaleString()}</p>
              </li>)}
            </ol>}
      </section>

      <section className="mt-8">
        <h3 className="flex items-center gap-2 text-[13px] font-medium text-slate-500"><UserRound size={15} />Check-in / check-out log</h3>
        {(assignments.data ?? []).length === 0
          ? <p className="mt-2 text-sm text-slate-500">This CI has never been checked out.</p>
          : <ol className="mt-2 space-y-2">
              {assignments.data!.map((entry) => <li key={entry.id} className="border-l-2 border-slate-200 pl-3 text-sm dark:border-slate-700">
                <p className="text-slate-700 dark:text-slate-200">{ciAssignmentLabel(entry.action)}: {describeAssignment(entry)}</p>
                {entry.note && <p className="text-slate-600 dark:text-slate-400">{entry.note}</p>}
                <p className="text-xs text-slate-500">{entry.actorId} · {new Date(entry.occurredAt).toLocaleString()}</p>
              </li>)}
            </ol>}
      </section>
    </div>
  </div>
}

function Field({ label, htmlFor, children }: { label: string; htmlFor: string; children: React.ReactNode }) {
  return <div>
    <label htmlFor={htmlFor} className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">{label}</label>
    {children}
  </div>
}
