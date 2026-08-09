import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { GitFork, Search, X } from 'lucide-react'
import { useEffect, useState } from 'react'
import { toast } from 'sonner'
import { assetsApi, ciTypeLabel, type Ci, type CiRelationship, type CiRelationshipType } from '../../api/assets'
import { ApiError } from '../../api/client'
import { Button } from '../../components/ui/Button'
import { ciLifecycleLabel } from './lifecycle'
import { ciRelationshipTypes, ciRelationshipVerb } from './relationships'

/**
 * The write half of the Relations card: pick the CI this one depends on, say how, and save. The CI on
 * screen is always the source, because WP-2.3 reads source → target as "source needs target" — the
 * reverse edge is created from the other CI's page, where that sentence is the one being written.
 *
 * Nothing here pre-filters the candidate list. Relating a CI to itself, to a disposed CI, or a pair that
 * already exists are all refused by the server (WP-2.3), and this dialog shows each refusal against the
 * chosen CI rather than quietly hiding the row and leaving the guard unprovable.
 */
export function CiRelateDialog({ ci, existing, onClose }: { ci: Ci | null; existing: CiRelationship[]; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [search, setSearch] = useState('')
  const [term, setTerm] = useState('')
  const [target, setTarget] = useState<Ci | null>(null)
  const [type, setType] = useState<CiRelationshipType>('DependsOn')
  const [description, setDescription] = useState('')

  useEffect(() => {
    const timer = window.setTimeout(() => setTerm(search), 200)
    return () => window.clearTimeout(timer)
  }, [search])

  const candidates = useQuery({
    queryKey: ['cis', { search: term, pageSize: 10 }],
    queryFn: () => assetsApi.listCis({ search: term, pageSize: 10 }),
    enabled: Boolean(ci),
    placeholderData: keepPreviousData,
  })

  const create = useMutation({
    mutationFn: () => assetsApi.createRelationship(ci!.id, {
      targetCiId: target!.id,
      type,
      description: description.trim() ? description.trim() : null,
    }),
    onSuccess: async (edge) => {
      // Both the edge list and every traversal that could have reached this CI are now stale.
      await queryClient.invalidateQueries({ queryKey: ['cis'] })
      toast.success(`${edge.sourceCiName} ${ciRelationshipVerb(edge.type)} ${edge.targetCiName}`)
      onClose()
    },
  })

  if (!ci) return null

  // Every refusal — the 400 field error, the duplicate 409 and the disposed 409 — is a statement about
  // the CI that was chosen, so they all land under it rather than in a toast the form cannot explain.
  const failure = create.error instanceof ApiError
    ? create.error.errors?.TargetCiId?.[0] ?? create.error.message
    : create.error?.message
  const relatedIds = new Set(existing.map((edge) => (edge.sourceCiId === ci.id ? edge.targetCiId : edge.sourceCiId)))

  return <div className="fixed inset-0 z-50 grid place-items-center bg-slate-950/50 p-4" role="presentation"
    onMouseDown={(event) => { if (event.target === event.currentTarget && !create.isPending) onClose() }}>
    <form role="dialog" aria-modal="true" aria-labelledby="relate-ci-title"
      className="max-h-[90vh] w-full max-w-xl overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={(event) => { event.preventDefault(); if (target) create.mutate() }}>
      <div className="mb-4 flex items-start">
        <div>
          <h2 id="relate-ci-title" className="text-lg font-semibold">Relate {ci.name} to…</h2>
          <p className="mt-1 text-sm text-slate-500">Record what this CI needs. Search the CMDB by name, asset tag, or serial number.</p>
        </div>
        <Button type="button" variant="ghost" className="ml-auto size-9 p-0" aria-label="Close" onClick={onClose}><X size={19} /></Button>
      </div>

      {target
        ? <div className="rounded-lg border border-slate-200 p-4 dark:border-slate-700">
            <div className="flex flex-wrap items-center gap-2">
              <span className="text-sm text-slate-500">Relationship</span>
              <Button type="button" variant="ghost" className="ml-auto h-8 px-2 text-[13px]"
                onClick={() => { setTarget(null); create.reset() }}>Choose another CI</Button>
            </div>
            <p className="mt-3 flex flex-wrap items-center gap-2 text-sm">
              <span className="font-medium">{ci.name}</span>
              <label className="sr-only" htmlFor="relate-type">Relationship type</label>
              <select id="relate-type" className="rounded-lg border border-slate-200 bg-white px-2 py-1 text-sm text-slate-900 focus:border-blue-600 focus:outline-none dark:border-slate-700 dark:bg-slate-900 dark:text-slate-100" value={type}
                onChange={(event) => { setType(event.target.value as CiRelationshipType); create.reset() }}>
                {ciRelationshipTypes.map((option) => <option key={option} value={option}>{ciRelationshipVerb(option)}</option>)}
              </select>
              <span className="font-medium">{target.name}</span>
            </p>
            <p className="mt-2 text-[13px] text-slate-500">{ciTypeLabel(target.type)} · {ciLifecycleLabel(target.lifecycleState)}</p>
            {failure && <p role="alert" className="mt-2 text-xs text-red-600">{failure}</p>}

            <label htmlFor="relate-description" className="mb-1.5 mt-4 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Description <span className="font-normal text-slate-400">(optional)</span></label>
            <textarea id="relate-description" rows={2} maxLength={500} className="input" value={description}
              onChange={(event) => setDescription(event.target.value)}
              placeholder="Why these two are related — the detail no traversal can infer." />
          </div>
        : <>
            <label className="flex h-10 items-center gap-2 rounded-lg border border-slate-200 px-3 text-slate-500 dark:border-slate-700">
              <Search size={17} /><span className="sr-only">Search configuration items</span>
              <input autoFocus value={search} onChange={(event) => setSearch(event.target.value)}
                className="w-full bg-transparent text-sm text-slate-900 outline-none dark:text-slate-100"
                placeholder="Search names, asset tags, and serials…" />
            </label>
            <ul className="mt-4 divide-y divide-slate-200 dark:divide-slate-800">
              {candidates.isLoading && <li className="py-6 text-center text-sm text-slate-500">Searching…</li>}
              {candidates.isError && <li role="alert" className="py-6 text-center text-sm text-red-600">Configuration items could not be loaded.</li>}
              {!candidates.isLoading && !candidates.isError && (candidates.data?.items.length ?? 0) === 0
                && <li className="py-6 text-center text-sm text-slate-500">No configuration items match.</li>}
              {candidates.data?.items.map((candidate) => <li key={candidate.id} className="flex items-center gap-3 py-3">
                <div className="min-w-0">
                  <p className="text-sm font-medium">{candidate.name}</p>
                  <p className="mt-0.5 text-[13px] text-slate-500">
                    {ciTypeLabel(candidate.type)}
                    {candidate.assetTag && <> · <span className="font-mono">{candidate.assetTag}</span></>}
                    {' '}· {ciLifecycleLabel(candidate.lifecycleState)}
                    {relatedIds.has(candidate.id) && <> · already related</>}
                  </p>
                </div>
                <Button type="button" variant="secondary" className="ml-auto h-9 shrink-0 text-[13px]"
                  onClick={() => { setTarget(candidate); create.reset() }}>Select</Button>
              </li>)}
            </ul>
          </>}

      <div className="mt-6 flex justify-end gap-2">
        <Button type="button" variant="secondary" disabled={create.isPending} onClick={onClose}>Cancel</Button>
        <Button type="submit" disabled={!target || create.isPending}>
          <GitFork size={16} />{create.isPending ? 'Saving…' : 'Create relationship'}
        </Button>
      </div>
    </form>
  </div>
}
