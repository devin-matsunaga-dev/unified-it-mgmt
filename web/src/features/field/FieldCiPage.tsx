import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowRight, MapPin, ScanLine, TicketPlus, UserRound } from 'lucide-react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { toast } from 'sonner'
import { assetsApi, ciTypeLabel, type CiLifecycleState } from '../../api/assets'
import { Button } from '../../components/ui/Button'
import { FieldActionBar } from '../../layout/FieldShell'
import { cn } from '../../lib/utils'
import { allowedTargets, ciLifecycleLabel, ciLifecycleTone } from '../assets/lifecycle'

/**
 * Where a scanned label lands on a phone (DESIGN.md §9). One screen, one question: is this the thing
 * I am holding, and what do I need to do to it. Everything the desktop CI page carries that a
 * technician cannot act on standing in a corridor — relationships, timeline, drift, coverage — is
 * simply absent: a handset cannot reach the agent shell at all, so nothing here links into it.
 */
export function FieldCiPage() {
  const { id = '' } = useParams()
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const ci = useQuery({ queryKey: ['ci', id], queryFn: () => assetsApi.getCi(id), enabled: Boolean(id) })
  const states = useQuery({ queryKey: ['ci-lifecycle-states'], queryFn: assetsApi.listLifecycleStates })

  const transition = useMutation({
    mutationFn: (target: CiLifecycleState) => assetsApi.transitionCi(id, target, null),
    onSuccess: async (updated) => {
      await queryClient.invalidateQueries({ queryKey: ['ci', id] })
      toast.success(`${updated.name} is now ${ciLifecycleLabel(updated.lifecycleState).toLowerCase()}`)
    },
    onError: (error: Error) => toast.error(error.message),
  })

  if (ci.isLoading) {
    return <div aria-label="Loading" className="space-y-3">
      <div className="h-28 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
      <div className="h-40 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" />
    </div>
  }

  if (ci.isError || !ci.data) {
    return <div role="alert" className="rounded-xl border border-slate-200 bg-white p-6 text-center dark:border-slate-800 dark:bg-slate-900">
      <h1 className="text-lg font-semibold">Asset not found</h1>
      <p className="mt-2 text-[15px] text-slate-500">This label points at an asset that no longer exists, or the network dropped on the way.</p>
      <Button className="mt-5 h-12 w-full" variant="secondary" onClick={() => void ci.refetch()}>Try again</Button>
      <Link to="/field/scan" className="mt-3 inline-block text-[15px] font-medium text-blue-600">Scan another</Link>
    </div>
  }

  const item = ci.data
  const targets = allowedTargets(states.data ?? [], item.lifecycleState)
  const place = [item.ownership.siteName, item.ownership.departmentName].filter(Boolean).join(' · ')

  return <>
    {/* Identity first, and large: the technician's opening question is whether they scanned the right object. */}
    <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
      <div className="flex items-start justify-between gap-3">
        <h1 className="text-[22px] font-bold leading-tight">{item.name}</h1>
        <span className={cn('shrink-0 rounded-md px-2 py-0.5 text-xs font-medium', ciLifecycleTone(item.lifecycleState))}>
          {ciLifecycleLabel(item.lifecycleState)}
        </span>
      </div>
      <p className="mt-1 text-[15px] text-slate-500">{ciTypeLabel(item.type)}</p>
      <dl className="mt-4 space-y-2 text-[15px]">
        {item.assetTag && <div className="flex gap-2"><dt className="w-28 shrink-0 text-slate-500">Asset tag</dt><dd className="font-medium tabular-nums">{item.assetTag}</dd></div>}
        {item.serialNumber && <div className="flex gap-2"><dt className="w-28 shrink-0 text-slate-500">Serial</dt><dd className="break-all font-medium tabular-nums">{item.serialNumber}</dd></div>}
        <div className="flex gap-2">
          <dt className="w-28 shrink-0 text-slate-500">Holder</dt>
          <dd className="flex items-center gap-1.5 font-medium">
            <UserRound size={16} className="text-slate-400" />{item.ownership.ownerName ?? 'Nobody'}
          </dd>
        </div>
        {place && <div className="flex gap-2">
          <dt className="w-28 shrink-0 text-slate-500">Location</dt>
          <dd className="flex items-center gap-1.5 font-medium"><MapPin size={16} className="text-slate-400" />{place}</dd>
        </div>}
      </dl>
    </section>

    <section className="mt-3 rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="text-base font-semibold">Move to</h2>
      {targets.length === 0
        ? <p className="mt-2 text-[15px] text-slate-500">
            {states.isLoading ? 'Checking what this can move to…' : `Nothing follows ${ciLifecycleLabel(item.lifecycleState).toLowerCase()} — this asset is at the end of its life.`}
          </p>
        // The legal next states come from the server's graph via allowedTargets, so the phone never
        // holds a second copy of the guard — same rule the desktop drawer follows.
        : <div className="mt-3 grid gap-2">
            {targets.map((target) => <Button
              key={target}
              variant="secondary"
              className="h-12 w-full justify-between text-[15px]"
              disabled={transition.isPending}
              onClick={() => transition.mutate(target)}
            >{ciLifecycleLabel(target)}<ArrowRight size={17} /></Button>)}
          </div>}
    </section>

    <FieldActionBar>
      <Button className="h-12 w-full text-[15px]" onClick={() => navigate(`/field/ci/${item.id}/ticket`)}>
        <TicketPlus size={18} />Open a ticket
      </Button>
      <Button variant="secondary" className="h-12 w-full text-[15px]" onClick={() => navigate('/field/scan')}>
        <ScanLine size={18} />Scan next
      </Button>
    </FieldActionBar>
  </>
}
