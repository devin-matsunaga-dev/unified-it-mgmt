import { Bookmark, Trash2, Users, X } from 'lucide-react'
import { useState } from 'react'
import type { TicketFilter, TicketView } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { cn } from '../../lib/utils'
import { filtersEqual, isFilterActive } from './ticketFilters'

export function SavedViews({ views, activeView, filter, pending, onApply, onSave, onUpdate, onDelete }: {
  views: TicketView[]
  activeView: TicketView | null
  filter: TicketFilter
  pending: boolean
  onApply: (view: TicketView | null) => void
  onSave: (input: { name: string; isShared: boolean }) => void
  onUpdate: (view: TicketView) => void
  onDelete: (view: TicketView) => void
}) {
  const [saveOpen, setSaveOpen] = useState(false)
  const dirty = activeView !== null && !filtersEqual(activeView.filter, filter)

  return <div className="flex flex-wrap items-center gap-2 border-b border-slate-200 px-4 py-3 dark:border-slate-800">
    <span className="text-[13px] font-medium text-slate-500">Views</span>
    <ViewChip label="All tickets" active={activeView === null} onClick={() => onApply(null)} />
    {views.map((view) => <ViewChip key={view.id} label={view.name} shared={view.isShared} active={activeView?.id === view.id} onClick={() => onApply(view)} />)}
    <div className="ml-auto flex items-center gap-2">
      {dirty && activeView.isMine && <Button variant="secondary" className="h-9" disabled={pending} onClick={() => onUpdate(activeView)}>Update view</Button>}
      {activeView?.isMine && <Button variant="ghost" className="h-9 px-2" aria-label={`Delete ${activeView.name}`} disabled={pending} onClick={() => onDelete(activeView)}><Trash2 size={16} /></Button>}
      <Button variant="secondary" className="h-9" disabled={!isFilterActive(filter)} onClick={() => setSaveOpen(true)}><Bookmark size={16} />Save view</Button>
    </div>
    {saveOpen && <SaveViewDialog pending={pending} onClose={() => setSaveOpen(false)} onSave={(input) => { onSave(input); setSaveOpen(false) }} />}
  </div>
}

function ViewChip({ label, shared, active, onClick }: { label: string; shared?: boolean; active: boolean; onClick: () => void }) {
  return <button type="button" aria-pressed={active} onClick={onClick} className={cn('inline-flex h-8 items-center gap-1.5 rounded-lg border px-3 text-[13px] font-medium transition-colors', active ? 'border-blue-600 bg-blue-600 text-white' : 'border-slate-200 text-slate-600 hover:bg-slate-50 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800')}>
    {shared && <Users size={14} />}{label}
  </button>
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
