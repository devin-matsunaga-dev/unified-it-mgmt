import { Bookmark, LayoutList, Trash2, Users, X } from 'lucide-react'
import { useEffect, useMemo, useState } from 'react'
import type { TicketFilter, TicketView } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import {
  isColumnVisible, moveColumn, readLayout, reconcileLayout, toggleItem, writeLayout,
} from '../../lib/tableLayout'
import { cn } from '../../lib/utils'
import { filtersEqual, isFilterActive } from './ticketFilters'
import { builtInView, builtInViewIds, builtInViewLayoutKey, offerableViews } from './ticketViews'

export function SavedViews({ views, activeView, filter, pending, username, onApply, onApplyFilter, onSave, onUpdate, onDelete }: {
  views: TicketView[]
  activeView: TicketView | null
  filter: TicketFilter
  pending: boolean
  onApply: (view: TicketView | null) => void
  /** Applies a built-in view, which has a filter but no record behind it. */
  onApplyFilter: (filter: TicketFilter) => void
  /** The signed-in sign-in name, for the view that is about the reader. Empty when unknown. */
  username: string
  onSave: (input: { name: string; isShared: boolean }) => void
  onUpdate: (view: TicketView) => void
  onDelete: (view: TicketView) => void
}) {
  const [saveOpen, setSaveOpen] = useState(false)
  const [menuOpen, setMenuOpen] = useState(false)
  const [layout, setLayout] = useState(() => readLayout(builtInViewLayoutKey, builtInViewIds))
  const [dragging, setDragging] = useState<string | null>(null)
  const dirty = activeView !== null && !filtersEqual(activeView.filter, filter)

  const offerable = offerableViews(username)

  /**
   * Every chip, defaults and saved views alike, in one arrangement.
   *
   * A saved view's id is prefixed so it can never be mistaken for a default's slug, and the whole
   * thing is reconciled on every render: a view somebody just saved, or one that has been deleted,
   * is not in the stored order, and without this it would be arranged but undrawable.
   */
  const chips: Chip[] = [
    ...offerable.map((view) => ({
      id: view.id,
      label: view.label,
      active: activeView === null && filtersEqual(view.filter(username), filter),
      apply: () => onApplyFilter(view.filter(username)),
      // Defaults are hidden and restored; a saved view is a record, and is deleted instead.
      remove: () => setLayout((current) => toggleItem(current, view.id)),
    })),
    ...views.map((view) => ({
      id: `${savedPrefix}${view.id}`,
      label: view.name,
      shared: view.isShared,
      active: activeView?.id === view.id,
      apply: () => onApply(view),
    })),
  ]

  /**
   * Memoised on the chip ids rather than the chip objects, which are rebuilt every render: without
   * it the arrangement is a new object each time and the effect below writes to storage constantly.
   */
  const chipIds = chips.map((chip) => chip.id)
  const chipKey = chipIds.join('|')
  // eslint-disable-next-line react-hooks/exhaustive-deps -- chipKey stands in for chipIds by value.
  const arrangement = useMemo(() => reconcileLayout(chipIds, layout), [chipKey, layout])
  const hidden = new Set(arrangement.hidden)
  const byId = new Map(chips.map((chip) => [chip.id, chip]))
  const shownChips = arrangement.order
    .filter((id) => !hidden.has(id))
    .map((id) => byId.get(id))
    .filter((chip): chip is Chip => chip !== undefined)

  useEffect(() => writeLayout(builtInViewLayoutKey, arrangement), [arrangement])

  return <div className="flex flex-wrap items-center gap-2 border-b border-slate-200 px-4 py-3 dark:border-slate-800">
    <span className="text-[13px] font-medium text-slate-500">Views</span>
    {shownChips.map((chip) => <ViewChip key={chip.id}
      label={chip.label}
      shared={chip.shared}
      // A default is active when the list is narrowed to exactly what it means: there is no record to
      // compare identities with. A saved view is active by its own id.
      active={chip.active}
      dragging={dragging === chip.id}
      onClick={chip.apply}
      onRemove={chip.remove}
      onDragStart={(event) => { setDragging(chip.id); if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move' }}
      onDragOver={(event) => { if (dragging && dragging !== chip.id) event.preventDefault() }}
      onDrop={(event) => {
        event.preventDefault()
        // Based on the reconciled arrangement: a chip that has just appeared is not in the stored
        // order yet, and moving it there would silently do nothing.
        if (dragging) setLayout(moveColumn(arrangement, dragging, chip.id))
        setDragging(null)
      }}
      onDragEnd={() => setDragging(null)} />)}
    <div className="ml-auto flex items-center gap-2">
      <div className="relative">
        <Button variant="ghost" className="h-9 px-2" aria-label="Default views" aria-expanded={menuOpen}
          onClick={() => setMenuOpen((value) => !value)}>
          <LayoutList size={16} />
        </Button>
        {menuOpen && <div className="absolute right-0 top-11 z-20 w-64 rounded-lg border border-slate-200 bg-white p-2 text-left shadow-sm dark:border-slate-700 dark:bg-slate-800">
          {offerable.map((view) => <label key={view.id} className="flex items-start gap-2 rounded px-2 py-1.5 text-sm hover:bg-slate-50 dark:hover:bg-slate-700">
            <input type="checkbox" className="mt-1" checked={isColumnVisible(layout, view.id)}
              onChange={() => setLayout((current) => toggleItem(current, view.id))} />
            <span className="min-w-0">
              <span className="block">{view.label}</span>
              <span className="block text-[12px] text-slate-500">{view.description}</span>
            </span>
          </label>)}
        </div>}
      </div>
      {dirty && activeView.isMine && <Button variant="secondary" className="h-9" disabled={pending} onClick={() => onUpdate(activeView)}>Update view</Button>}
      {activeView?.canDelete && <Button variant="ghost" className="h-9 px-2" aria-label={`Delete ${activeView.name}`} disabled={pending} onClick={() => onDelete(activeView)}><Trash2 size={16} /></Button>}
      <Button variant="secondary" className="h-9" disabled={!isFilterActive(filter)} onClick={() => setSaveOpen(true)}><Bookmark size={16} />Save view</Button>
    </div>
    {saveOpen && <SaveViewDialog pending={pending} onClose={() => setSaveOpen(false)} onSave={(input) => { onSave(input); setSaveOpen(false) }} />}
  </div>
}

/** A saved view's id, namespaced so it can never collide with a default's slug. */
const savedPrefix = 'saved:'

type Chip = {
  id: string
  label: string
  shared?: boolean
  active: boolean
  apply: () => void
  /** Defaults only: a saved view is removed by deleting the record, not by hiding a chip. */
  remove?: () => void
}

function ViewChip({ label, shared, active, dragging, onClick, onRemove, ...drag }: {
  label: string
  shared?: boolean
  active: boolean
  dragging: boolean
  onClick: () => void
  onRemove?: () => void
  onDragStart: (event: React.DragEvent) => void
  onDragOver: (event: React.DragEvent) => void
  onDrop: (event: React.DragEvent) => void
  onDragEnd: () => void
}) {
  return <span
    draggable
    {...drag}
    className={cn('inline-flex h-8 cursor-grab items-center rounded-lg border text-[13px] font-medium transition-colors', dragging && 'opacity-40', active ? 'border-blue-600 bg-blue-600 text-white' : 'border-slate-200 text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800')}>
    <button type="button" aria-pressed={active} onClick={onClick} className="inline-flex h-full items-center gap-1.5 rounded-l-lg px-3">
      {shared && <Users size={14} />}{label}
    </button>
    {onRemove && <button type="button" aria-label={`Remove ${label}`} onClick={onRemove}
      className={cn('grid h-full place-items-center rounded-r-lg pl-0.5 pr-2', active ? 'text-white/70 hover:text-white' : 'text-slate-400 hover:text-slate-600 dark:hover:text-slate-200')}>
      <X size={13} />
    </button>}
  </span>
}

function SaveViewDialog({ pending, onClose, onSave }: { pending: boolean; onClose: () => void; onSave: (input: { name: string; isShared: boolean }) => void }) {
  const [name, setName] = useState('')
  const [isShared, setIsShared] = useState(false)
  return <div className="fixed inset-0 z-50 grid place-items-center bg-slate-950/50 p-4" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
    <section role="dialog" aria-modal="true" aria-labelledby="save-view-title" className="w-full max-w-md rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <div className="mb-5 flex items-center"><div><h2 id="save-view-title" className="text-lg font-semibold">Save view</h2><p className="mt-1 text-sm text-slate-500">Keeps the current search and filters under a name you can come back to.</p></div><Button variant="ghost" className="ml-auto size-9 p-0" aria-label="Close" onClick={onClose}><X size={19} /></Button></div>
      <form onSubmit={(event) => { event.preventDefault(); if (name.trim()) onSave({ name: name.trim(), isShared }) }} className="space-y-4">
        <label className="block"><span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Name</span><input autoFocus className="input" value={name} maxLength={100} onChange={(event) => setName(event.target.value)} /></label>
        <label className="flex items-center gap-2 text-sm text-slate-600 dark:text-slate-300"><input type="checkbox" checked={isShared} onChange={(event) => setIsShared(event.target.checked)} />Share with the team</label>
        <div className="flex justify-end gap-3"><Button type="button" variant="secondary" onClick={onClose}>Cancel</Button><Button type="submit" disabled={!name.trim() || pending}>Save view</Button></div>
      </form>
    </section>
  </div>
}
