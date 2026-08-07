import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { MessageSquareQuote, Pencil, Plus, Trash2, X } from 'lucide-react'
import { useRef, useState } from 'react'
import { toast } from 'sonner'
import { helpdeskApi, type CannedResponse } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { insertCannedResponse } from './cannedResponse'

const placeholders = ['{{ticket.number}}', '{{ticket.title}}', '{{requester.name}}', '{{agent.name}}']

export function CannedResponsePicker({ ticketId, value, onChange }: { ticketId: string; value: string; onChange: (text: string) => void }) {
  const [manageOpen, setManageOpen] = useState(false)
  // What the last insertion put in the box, so cycling through templates swaps it instead of stacking.
  const lastInserted = useRef<string | null>(null)
  const responses = useQuery({ queryKey: ['canned-responses'], queryFn: helpdeskApi.listCannedResponses })
  const insert = useMutation({
    mutationFn: (id: string) => helpdeskApi.renderCannedResponse(id, ticketId),
    onSuccess: (rendered) => {
      const { text, inserted } = insertCannedResponse(value, rendered.body, lastInserted.current)
      lastInserted.current = inserted
      onChange(text)
      toast.success(`Inserted "${rendered.name}"`)
    },
  })

  return <div className="mb-3 flex flex-wrap items-center gap-2">
    <label className="flex items-center gap-2 text-[13px] font-medium text-slate-500"><MessageSquareQuote size={16} /><span className="sr-only">Insert canned response</span>
      <select aria-label="Insert canned response" className="input w-auto min-w-52" value="" disabled={insert.isPending || responses.isLoading} onChange={(event) => { if (event.target.value) insert.mutate(event.target.value) }}>
        <option value="">{responses.isLoading ? 'Loading canned responses…' : 'Insert a canned response…'}</option>
        {responses.data?.map((response) => <option key={response.id} value={response.id}>{response.name}</option>)}
      </select>
    </label>
    <Button type="button" variant="ghost" onClick={() => setManageOpen(true)}>Manage</Button>
    {insert.error && <span role="alert" className="text-sm text-red-600">{insert.error.message}</span>}
    {manageOpen && <ManageCannedResponses responses={responses.data ?? []} onClose={() => setManageOpen(false)} />}
  </div>
}

function ManageCannedResponses({ responses, onClose }: { responses: CannedResponse[]; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState<CannedResponse | null>(null)
  const [name, setName] = useState('')
  const [body, setBody] = useState('')
  const [confirmingId, setConfirmingId] = useState<string | null>(null)
  const refresh = () => queryClient.invalidateQueries({ queryKey: ['canned-responses'] })
  const reset = () => { setEditing(null); setName(''); setBody('') }
  const save = useMutation({
    mutationFn: () => editing ? helpdeskApi.updateCannedResponse(editing.id, { name, body }) : helpdeskApi.createCannedResponse({ name, body }),
    onSuccess: async (response) => { await refresh(); reset(); toast.success(`"${response.name}" saved`) },
  })
  const remove = useMutation({
    mutationFn: (id: string) => helpdeskApi.deleteCannedResponse(id),
    onSuccess: async () => { await refresh(); setConfirmingId(null); reset(); toast.success('Canned response deleted') },
  })

  return <div className="fixed inset-0 z-50 grid place-items-center bg-slate-950/50 p-4" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget) onClose() }}>
    <section role="dialog" aria-modal="true" aria-labelledby="canned-responses-title" className="max-h-[90vh] w-full max-w-2xl overflow-y-auto rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <div className="mb-5 flex items-center"><div><h2 id="canned-responses-title" className="text-lg font-semibold">Canned responses</h2><p className="mt-1 text-sm text-slate-500">Placeholders {placeholders.join(' ')} are filled in when the response is inserted.</p></div><Button variant="ghost" className="ml-auto size-9 p-0" aria-label="Close" onClick={onClose}><X size={19} /></Button></div>
      <ul className="divide-y divide-slate-200 dark:divide-slate-800">
        {responses.length === 0 && <li className="py-6 text-center text-sm text-slate-500">No canned responses yet.</li>}
        {responses.map((response) => <li key={response.id} className="flex items-start gap-3 py-3">
          <div className="min-w-0"><p className="text-sm font-medium">{response.name}</p><p className="mt-1 line-clamp-2 text-sm text-slate-500">{response.body}</p></div>
          <div className="ml-auto flex shrink-0 gap-1">
            <Button variant="ghost" className="size-9 p-0" aria-label={`Edit ${response.name}`} onClick={() => { setEditing(response); setName(response.name); setBody(response.body); setConfirmingId(null) }}><Pencil size={16} /></Button>
            {confirmingId === response.id
              ? <Button variant="secondary" className="h-9 text-red-600" disabled={remove.isPending} onClick={() => remove.mutate(response.id)}>Confirm delete</Button>
              : <Button variant="ghost" className="size-9 p-0 text-red-600" aria-label={`Delete ${response.name}`} onClick={() => setConfirmingId(response.id)}><Trash2 size={16} /></Button>}
          </div>
        </li>)}
      </ul>
      <form className="mt-5 space-y-3 border-t border-slate-200 pt-5 dark:border-slate-800" onSubmit={(event) => { event.preventDefault(); if (name.trim() && body.trim()) save.mutate() }}>
        <h3 className="text-sm font-semibold">{editing ? `Edit "${editing.name}"` : 'New canned response'}</h3>
        <label className="block"><span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Name</span><input className="input" maxLength={100} value={name} onChange={(event) => setName(event.target.value)} /></label>
        <label className="block"><span className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Body</span><textarea className="input min-h-28 resize-y" maxLength={10_000} value={body} onChange={(event) => setBody(event.target.value)} /></label>
        {(save.error ?? remove.error) && <p role="alert" className="text-sm text-red-600">{(save.error ?? remove.error)!.message}</p>}
        <div className="flex justify-end gap-3">{editing && <Button type="button" variant="secondary" onClick={reset}>Cancel edit</Button>}<Button type="submit" disabled={!name.trim() || !body.trim() || save.isPending}><Plus size={16} />{editing ? 'Save changes' : 'Add response'}</Button></div>
      </form>
    </section>
  </div>
}
