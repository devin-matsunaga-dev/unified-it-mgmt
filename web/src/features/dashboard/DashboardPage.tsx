import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { LayoutGrid, Move, Plus, RotateCcw, Save, Trash2, X } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { toast } from 'sonner'
import { dashboardApi, type DashboardPlacement, type DashboardWidgetType } from '../../api/dashboard'
import { Button } from '../../components/ui/Button'
import { cn } from '../../lib/utils'
import { DashboardWidgetCard } from './DashboardWidgetCard'
import {
  addWidget,
  layoutsEqual,
  moveWidget,
  offerableWidgets,
  placedWidgets,
  removeWidget,
  replaceWidget,
  setDisplay,
  setWidth,
  unplacedWidgets,
  widgetIcon,
  widthClass,
} from './dashboardUi'

/**
 * The unified dashboard (WP-5.5): every module's headline numbers on one screen, in an order this person
 * chose, with every number a link into the list it counts.
 *
 * What is drawn — which widgets exist, what they say, what tone each number carries — is entirely the
 * server's. What is decided here is only the arrangement: which view is on screen, where the cards sit, how
 * wide they are, and when that is worth saving.
 */
export function DashboardPage() {
  const queryClient = useQueryClient()
  const dashboard = useQuery({ queryKey: ['dashboard'], queryFn: dashboardApi.get })
  const [arranging, setArranging] = useState(false)
  const [draft, setDraft] = useState<DashboardPlacement[] | null>(null)
  const [naming, setNaming] = useState<string | null>(null)
  const [addOpen, setAddOpen] = useState(false)
  const [dragging, setDragging] = useState<number | null>(null)
  const [dropTarget, setDropTarget] = useState<number | null>(null)
  const addRef = useRef<HTMLDivElement>(null)

  const saved = dashboard.data?.layout.placements
  const viewId = dashboard.data?.layout.viewId ?? null

  // The draft follows the server until somebody edits. Without this, a view saved in another tab — or a
  // switch to a different view — would never reach the screen. Keyed on the view as well as the
  // placements, so switching to a view with the same cards still resets the draft.
  useEffect(() => {
    setDraft(null)
  }, [viewId])

  const placements = draft ?? saved ?? []
  const dirty = saved !== undefined && !layoutsEqual(placements, saved)

  const refresh = () => queryClient.invalidateQueries({ queryKey: ['dashboard'] })
  const settle = async (message: string) => {
    await refresh()
    setDraft(null)
    setArranging(false)
    toast.success(message)
  }

  const saveView = useMutation({
    mutationFn: (next: DashboardPlacement[]) => dashboardApi.saveView(viewId!, { placements: next }),
    onSuccess: () => settle('View saved'),
    onError: (error: Error) => toast.error(error.message),
  })

  const createView = useMutation({
    mutationFn: ({ name, next }: { name: string; next: DashboardPlacement[] }) =>
      dashboardApi.createView(name, next),
    onSuccess: async () => { setNaming(null); await settle('View created') },
    onError: (error: Error) => toast.error(error.message),
  })

  const selectView = useMutation({
    mutationFn: (id: string) => dashboardApi.selectView(id),
    onSuccess: async () => { await refresh(); setDraft(null); setArranging(false) },
    onError: (error: Error) => toast.error(error.message),
  })

  const deleteView = useMutation({
    mutationFn: (id: string) => dashboardApi.deleteView(id),
    onSuccess: () => settle('View deleted'),
    onError: (error: Error) => toast.error(error.message),
  })

  useEffect(() => {
    if (!addOpen) return
    const close = (event: MouseEvent) => {
      if (!addRef.current?.contains(event.target as Node)) setAddOpen(false)
    }
    document.addEventListener('mousedown', close)
    return () => document.removeEventListener('mousedown', close)
  }, [addOpen])

  const data = dashboard.data
  const cards = data
    ? placedWidgets({ ...data, layout: { ...data.layout, placements } })
    : []
  const choices = data ? offerableWidgets(data) : []
  const addable = data ? unplacedWidgets(data, placements) : []
  const indexOf = (type: DashboardWidgetType) =>
    placements.findIndex((placement) => placement.type === type)

  // Saving the role default has to create a view first — there is nothing to write to until then, which is
  // also the moment somebody has to name it.
  const save = () => viewId ? saveView.mutate(placements) : setNaming('')
  const edit = (next: DashboardPlacement[]) => setDraft(next)

  return <div className="space-y-5">
    <div className="flex flex-wrap items-center gap-2">
      <div role="tablist" aria-label="Dashboard views" className="flex flex-wrap items-center gap-1.5">
        {data?.views.length === 0 && <span
          className="rounded-lg bg-slate-100 px-3 py-1.5 text-sm font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">
          {data.layout.preset === 'Executive' ? 'Executive default' : 'Operations default'}
        </span>}
        {data?.views.map((view) => <button key={view.id} type="button" role="tab"
          aria-selected={view.isActive}
          onClick={() => !view.isActive && selectView.mutate(view.id)}
          className={cn('rounded-lg border px-3 py-1.5 text-sm transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600',
            view.isActive
              ? 'border-blue-600 bg-blue-600 text-white'
              : 'border-slate-200 bg-white text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-300 dark:hover:bg-slate-800')}>
          {view.name}
        </button>)}
        <Button variant="ghost" className="h-8 px-2 text-[13px]" onClick={() => setNaming('')}>
          <Plus size={15} />New view
        </Button>
      </div>

      <div className="ml-auto flex flex-wrap items-center gap-2">
        {dirty && <span className="text-[13px] text-slate-500">Unsaved changes</span>}
        {dirty && <Button variant="ghost" className="h-9" onClick={() => setDraft(null)}>
          <RotateCcw size={15} />Discard
        </Button>}

        {addable.length > 0 && <div ref={addRef} className="relative">
          <Button variant="secondary" className="h-9" aria-haspopup="menu" aria-expanded={addOpen}
            onClick={() => setAddOpen((current) => !current)}>
            <Plus size={15} />Add card
          </Button>
          {addOpen && <div role="menu" aria-label="Add a card"
            className="absolute right-0 top-[calc(100%+6px)] z-30 w-60 rounded-xl border border-slate-200 bg-white p-1.5 shadow-lg dark:border-slate-700 dark:bg-slate-900">
            {addable.map((widget) => {
              const Icon = widgetIcon(widget.type)
              return <button key={widget.type} type="button" role="menuitem"
                onClick={() => { edit(addWidget(placements, widget.type)); setAddOpen(false) }}
                className="flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-sm hover:bg-slate-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:hover:bg-slate-800">
                <Icon size={15} aria-hidden className="shrink-0 text-slate-400" />
                <span className="min-w-0 truncate">{widget.title}</span>
              </button>
            })}
          </div>}
        </div>}

        <Button variant={arranging ? 'primary' : 'secondary'} className="h-9"
          aria-pressed={arranging}
          disabled={cards.length === 0}
          onClick={() => setArranging((current) => !current)}>
          {arranging ? <><X size={15} />Done arranging</> : <><Move size={15} />Arrange</>}
        </Button>

        {viewId && <Button variant="ghost" className="h-9 text-red-600 hover:bg-red-50 dark:hover:bg-red-500/10"
          aria-label={`Delete ${data?.layout.name}`}
          onClick={() => deleteView.mutate(viewId)}>
          <Trash2 size={15} />Delete view
        </Button>}

        <Button className="h-9" disabled={!dirty || saveView.isPending} onClick={save}>
          <Save size={15} />{saveView.isPending ? 'Saving…' : 'Save'}
        </Button>
      </div>
    </div>

    {arranging && <p className="rounded-lg border border-dashed border-slate-300 bg-slate-50 px-4 py-2 text-[13px] text-slate-600 dark:border-slate-700 dark:bg-slate-800/50 dark:text-slate-300">
      Drag a card onto another to reorder them, or use a card's own menu. Links are paused while you arrange.
    </p>}

    {dashboard.isLoading
      // Skeleton blocks rather than a spinner (DESIGN §10), in the shape the cards will land in.
      ? <div aria-label="Loading dashboard" className="grid gap-5 md:grid-cols-12">
          {[0, 1, 2, 3, 4].map((key) => <div key={key}
            className={cn('h-56 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800',
              key < 3 ? 'md:col-span-6 xl:col-span-4' : key === 3 ? 'md:col-span-6 xl:col-span-8' : 'md:col-span-6 xl:col-span-4')} />)}
        </div>
      : dashboard.isError || !data
        // A failed read is a fact about the request, and it must not look like an estate with nothing in it.
        ? <div role="alert" className="rounded-xl border border-slate-200 bg-white p-10 text-center dark:border-slate-800 dark:bg-slate-900">
            <p className="text-sm text-slate-600 dark:text-slate-300">The dashboard could not be loaded.</p>
            <Button variant="secondary" className="mt-4" onClick={() => void dashboard.refetch()}>Try again</Button>
          </div>
        : cards.length === 0
          ? <Empty hasWidgets={choices.length > 0} onAdd={() => setAddOpen(true)} />
          : <div className="grid gap-5 md:grid-cols-12">
              {cards.map(({ placement, widget }, index) => <div key={placement.type}
                className={cn('flex', widthClass[placement.width])}>
                <div className="flex-1">
                  <DashboardWidgetCard
                    widget={widget}
                    placement={placement}
                    arranging={arranging}
                    dragging={dragging === index}
                    dropTarget={dropTarget === index}
                    controls={{
                      index,
                      count: cards.length,
                      choices,
                      placedTypes: placements.map((item) => item.type),
                      // Looked up by type rather than trusting the drawn position: the two agree today,
                      // and a card that ever stops being drawn would otherwise move its neighbour.
                      onMove: (to) => edit(moveWidget(placements, indexOf(placement.type), to)),
                      onWidth: (width) => edit(setWidth(placements, indexOf(placement.type), width)),
                      onDisplay: (shape) => edit(setDisplay(placements, indexOf(placement.type), shape)),
                      onReplace: (type) => edit(replaceWidget(placements, indexOf(placement.type), type)),
                      onRemove: () => edit(removeWidget(placements, indexOf(placement.type))),
                      onDragStart: () => setDragging(index),
                      onDragOverCard: () => setDropTarget(index),
                      onDropOn: () => {
                        if (dragging !== null) edit(moveWidget(placements, dragging, index))
                        setDragging(null)
                        setDropTarget(null)
                      },
                      onDragEnd: () => { setDragging(null); setDropTarget(null) },
                    }} />
                </div>
              </div>)}
            </div>}

    {naming !== null && <NameDialog
      value={naming}
      pending={createView.isPending}
      onChange={setNaming}
      onCancel={() => setNaming(null)}
      // A view created from the toolbar is a blank slate; one created because somebody saved a rearranged
      // default keeps what they had just arranged.
      onSubmit={(name) => createView.mutate({ name, next: dirty ? placements : [] })} />}
  </div>
}

/**
 * Not a bare "No data" (DESIGN §6). Two different empty states, because they need two different answers:
 * a view somebody emptied wants a card adding, and an account with no widgets at all wants explaining.
 */
function Empty({ hasWidgets, onAdd }: { hasWidgets: boolean; onAdd: () => void }) {
  return <div className="rounded-xl border border-slate-200 bg-white p-10 text-center dark:border-slate-800 dark:bg-slate-900">
    <span className="mx-auto mb-3 grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15">
      <LayoutGrid size={22} />
    </span>
    {hasWidgets
      ? <>
          <p className="text-sm font-medium">This view is empty</p>
          <p className="mt-1 text-sm text-slate-500">Add a card to start building it.</p>
          <Button variant="secondary" className="mt-4" onClick={onAdd}><Plus size={15} />Add card</Button>
        </>
      : <>
          <p className="text-sm font-medium">There are no widgets for your account</p>
          <p className="mt-1 text-sm text-slate-500">
            The dashboard shows service desk, network and licensing summaries, which need an operator role.
          </p>
        </>}
  </div>
}

/** Quick-create in a modal, following DESIGN §6 and the shape WP-4.4's licence pool dialog set. */
function NameDialog({ value, pending, onChange, onCancel, onSubmit }: {
  value: string
  pending: boolean
  onChange: (value: string) => void
  onCancel: () => void
  onSubmit: (name: string) => void
}) {
  const name = value.trim()
  return <div className="fixed inset-0 z-40 grid place-items-center bg-slate-900/40 p-4"
    role="dialog" aria-modal="true" aria-label="New view">
    <form className="w-full max-w-md rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={(event) => { event.preventDefault(); if (name) onSubmit(name) }}>
      <h2 className="text-lg font-semibold">New view</h2>
      <p className="mt-1 text-sm text-slate-500">
        A view is your own arrangement of the dashboard. You can keep several and switch between them.
      </p>
      <label className="mt-4 block text-[13px] font-medium" htmlFor="dashboard-view-name">Name</label>
      <input id="dashboard-view-name" autoFocus value={value} maxLength={60}
        onChange={(event) => onChange(event.target.value)}
        placeholder="Morning check"
        className="mt-1 h-10 w-full rounded-lg border border-slate-200 px-3 text-sm outline-none focus:border-blue-600 focus:ring-2 focus:ring-blue-600/20 dark:border-slate-700 dark:bg-slate-900" />
      <div className="mt-6 flex justify-end gap-2">
        <Button type="button" variant="secondary" onClick={onCancel}>Cancel</Button>
        <Button type="submit" disabled={!name || pending}>{pending ? 'Creating…' : 'Create view'}</Button>
      </div>
    </form>
  </div>
}
