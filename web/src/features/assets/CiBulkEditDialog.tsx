import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Layers, X } from 'lucide-react'
import { useEffect, useState, type ReactNode } from 'react'
import { toast } from 'sonner'
import { assetsApi, type BulkEditReport, type Ci, type CiLifecycleState } from '../../api/assets'
import { directoryApi } from '../../api/directory'
import { Button } from '../../components/ui/Button'
import { ciLifecycleLabel, ciLifecycleStates } from './lifecycle'

/**
 * Bulk edit of a selection (DESIGN.md §6 modal). Ownership and the lifecycle move are separate opt-ins
 * because ownership is a complete statement — sending it with nobody selected checks every CI in, which
 * must never happen just because the operator wanted to change the state.
 *
 * A CI the server refuses (an illegal transition, a disposed record) is reported per row; the rest of
 * the selection still applies.
 */
export function CiBulkEditDialog({ selection, onClose, onApplied }: {
  selection: Ci[]
  onClose: () => void
  onApplied: () => void
}) {
  const queryClient = useQueryClient()
  const [changeOwnership, setChangeOwnership] = useState(false)
  const [changeLifecycle, setChangeLifecycle] = useState(false)
  const [ownerUserId, setOwnerUserId] = useState('')
  const [departmentId, setDepartmentId] = useState('')
  const [siteId, setSiteId] = useState('')
  const [lifecycleState, setLifecycleState] = useState<CiLifecycleState>('Deployed')
  const [note, setNote] = useState('')
  const [report, setReport] = useState<BulkEditReport | null>(null)

  const open = selection.length > 0
  useEffect(() => {
    if (!open) return
    setChangeOwnership(false); setChangeLifecycle(false)
    setOwnerUserId(''); setDepartmentId(''); setSiteId('')
    setLifecycleState('Deployed'); setNote(''); setReport(null)
  }, [open, selection.length])

  const users = useQuery({ queryKey: ['directory', 'users'], queryFn: directoryApi.listUsers, enabled: open })
  const departments = useQuery({ queryKey: ['directory', 'departments'], queryFn: directoryApi.listDepartments, enabled: open })
  const sites = useQuery({ queryKey: ['directory', 'sites'], queryFn: directoryApi.listSites, enabled: open })

  const apply = useMutation({
    mutationFn: () => assetsApi.bulkEditCis({
      ciIds: selection.map((ci) => ci.id),
      ownership: changeOwnership
        ? { ownerUserId: ownerUserId || null, departmentId: departmentId || null, siteId: siteId || null }
        : undefined,
      lifecycleState: changeLifecycle ? lifecycleState : undefined,
      note: note.trim() || null,
    }),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ['cis'] })
      setReport(result)
      if (result.failed === 0) {
        toast.success(`${result.succeeded} configuration items updated`)
        onApplied()
      } else {
        toast.error(`${result.failed} of ${result.total} could not be changed`)
      }
    },
    onError: (error: Error) => toast.error(error.message),
  })

  if (!open) return null

  // Selecting a person prefills their department and site, as the single-CI drawer does.
  const selectOwner = (id: string) => {
    setOwnerUserId(id)
    const user = users.data?.find((candidate) => candidate.id === id)
    if (user) { setDepartmentId(user.departmentId); setSiteId(user.siteId) }
  }

  const nothingChosen = !changeOwnership && !changeLifecycle

  return <div className="fixed inset-0 z-20 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label={`Bulk edit ${selection.length} configuration items`}>
    <div className="max-h-full w-full max-w-lg overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <div className="flex items-start gap-3">
        <span className="grid size-9 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/10"><Layers size={18} /></span>
        <div>
          <h2 className="text-lg font-semibold">Bulk edit</h2>
          <p className="mt-1 text-sm text-slate-500">{selection.length} configuration items selected</p>
        </div>
        <Button variant="ghost" className="ml-auto size-9 p-0" onClick={onClose} aria-label="Close"><X size={18} /></Button>
      </div>

      {report
        ? <Report report={report} onClose={onClose} />
        : <>
            <section className="mt-6 space-y-3">
              <label className="flex items-center gap-2 text-sm font-medium">
                <input type="checkbox" checked={changeOwnership} onChange={(event) => setChangeOwnership(event.target.checked)} />
                Change owner and location
              </label>
              {changeOwnership && <div className="space-y-3 border-l-2 border-slate-200 pl-4 dark:border-slate-700">
                <Field label="Owner" htmlFor="bulk-owner">
                  <select id="bulk-owner" className="input h-11" value={ownerUserId} onChange={(event) => selectOwner(event.target.value)}>
                    <option value="">Unassigned (check in)</option>
                    {(users.data ?? []).map((user) => <option key={user.id} value={user.id}>{user.displayName}</option>)}
                  </select>
                </Field>
                <Field label="Department" htmlFor="bulk-department">
                  <select id="bulk-department" className="input h-11" value={departmentId} onChange={(event) => setDepartmentId(event.target.value)}>
                    <option value="">No department</option>
                    {(departments.data ?? []).map((department) => <option key={department.id} value={department.id}>{department.name}</option>)}
                  </select>
                </Field>
                <Field label="Location" htmlFor="bulk-site">
                  <select id="bulk-site" className="input h-11" value={siteId} onChange={(event) => setSiteId(event.target.value)}>
                    <option value="">No location</option>
                    {(sites.data ?? []).map((site) => <option key={site.id} value={site.id}>{site.name}</option>)}
                  </select>
                </Field>
                <p className="text-xs text-slate-500">Ownership is replaced outright: leaving the owner unassigned checks every selected CI in.</p>
              </div>}
            </section>

            <section className="mt-5 space-y-3">
              <label className="flex items-center gap-2 text-sm font-medium">
                <input type="checkbox" checked={changeLifecycle} onChange={(event) => setChangeLifecycle(event.target.checked)} />
                Move to a lifecycle state
              </label>
              {changeLifecycle && <div className="space-y-3 border-l-2 border-slate-200 pl-4 dark:border-slate-700">
                <Field label="Lifecycle state" htmlFor="bulk-lifecycle">
                  <select id="bulk-lifecycle" className="input h-11" value={lifecycleState} onChange={(event) => setLifecycleState(event.target.value as CiLifecycleState)}>
                    {ciLifecycleStates.map((state) => <option key={state} value={state}>{ciLifecycleLabel(state)}</option>)}
                  </select>
                </Field>
                <p className="text-xs text-slate-500">Each CI moves through the same guarded transition as it would alone; any that cannot make the move are listed afterwards.</p>
              </div>}
            </section>

            <section className="mt-5">
              <Field label="Note" htmlFor="bulk-note">
                <input id="bulk-note" className="input h-11" value={note} placeholder="Why is this changing?" onChange={(event) => setNote(event.target.value)} />
              </Field>
            </section>

            <div className="mt-6 flex gap-2">
              <Button disabled={nothingChosen || apply.isPending} onClick={() => apply.mutate()}>
                {apply.isPending ? 'Applying…' : `Apply to ${selection.length} items`}
              </Button>
              <Button variant="secondary" onClick={onClose}>Cancel</Button>
            </div>
            {nothingChosen && <p className="mt-3 text-[13px] text-slate-500">Choose an ownership change, a lifecycle state, or both.</p>}
          </>}
    </div>
  </div>
}

function Report({ report, onClose }: { report: BulkEditReport; onClose: () => void }) {
  const failures = report.rows.filter((row) => !row.succeeded)
  return <div className="mt-6">
    <p className="text-sm text-slate-600 dark:text-slate-300">{report.succeeded} of {report.total} updated.</p>
    {failures.length > 0 && <ul className="mt-3 space-y-2">
      {failures.map((row) => <li key={row.ciId} className="rounded-lg border border-red-200 bg-red-50 p-3 text-[13px] text-red-700 dark:border-red-500/30 dark:bg-red-500/10 dark:text-red-400">
        <span className="font-medium">{row.name ?? row.ciId}</span>: {row.error}
      </li>)}
    </ul>}
    <div className="mt-6"><Button onClick={onClose}>Close</Button></div>
  </div>
}

function Field({ label, htmlFor, children }: { label: string; htmlFor: string; children: ReactNode }) {
  return <div>
    <label htmlFor={htmlFor} className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">{label}</label>
    {children}
  </div>
}
