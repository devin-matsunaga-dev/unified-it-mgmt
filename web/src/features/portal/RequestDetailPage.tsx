import { useMutation, useQueries, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, CheckCircle2, MessageSquare, Send } from 'lucide-react'
import { useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { toast } from 'sonner'
import { helpdeskApi } from '../../api/helpdesk'
import { Button } from '../../components/ui/Button'
import { PortalErrorState } from '../../layout/PortalShell'
import { StatusPill, formatLocal } from '../tickets/ticketUi'

export function RequestDetailPage() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  const [reply, setReply] = useState('')
  const [confirmClose, setConfirmClose] = useState(false)
  const [request, comments] = useQueries({ queries: [
    { queryKey: ['tickets', id], queryFn: () => helpdeskApi.getTicket(id), enabled: Boolean(id) },
    { queryKey: ['tickets', id, 'comments'], queryFn: () => helpdeskApi.getComments(id), enabled: Boolean(id) },
  ] })
  const addComment = useMutation({
    mutationFn: () => helpdeskApi.addComment(id, reply, false),
    onSuccess: async () => {
      setReply('')
      await queryClient.invalidateQueries({ queryKey: ['tickets', id, 'comments'] })
      toast.success('Reply sent to the service desk')
    },
  })
  const close = useMutation({
    mutationFn: () => helpdeskApi.transition(id, 'Closed', null),
    onSuccess: async () => {
      setConfirmClose(false)
      await queryClient.invalidateQueries({ queryKey: ['tickets', id] })
      await queryClient.invalidateQueries({ queryKey: ['tickets'] })
      toast.success('Request closed. Thanks for confirming.')
    },
  })

  if (request.isLoading) return <div aria-label="Loading request" className="space-y-6"><div className="h-28 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" /><div className="h-96 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" /></div>
  if (request.isError || !request.data) return <PortalErrorState title="This request could not be loaded" message={request.error instanceof Error ? request.error.message : 'It may have been removed, or it belongs to someone else.'} retry={() => void request.refetch()} />

  const item = request.data
  const thread = comments.data ?? []
  const isResolved = item.status === 'Resolved'
  const isClosed = item.status === 'Closed'

  return <div className="space-y-6">
    <Link to="/portal" className="inline-flex items-center gap-2 text-sm text-slate-500 hover:text-blue-600"><ArrowLeft size={17} />Back to my requests</Link>

    <header>
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-mono text-sm text-slate-500">#{item.number}</span>
        <StatusPill status={item.status} />
      </div>
      <h1 className="mt-2 text-[32px] font-bold leading-tight">{item.title}</h1>
      <p className="mt-2 text-sm text-slate-500">Submitted {formatLocal(item.createdAt)} · last updated {formatLocal(item.updatedAt)}</p>
    </header>

    {isResolved && <section className="rounded-xl border border-green-200 bg-green-50 p-6 dark:border-green-900 dark:bg-green-950/40">
      <div className="flex flex-wrap items-center gap-4">
        <span className="grid size-11 place-items-center rounded-full bg-green-100 text-green-700 dark:bg-green-900 dark:text-green-300"><CheckCircle2 size={22} /></span>
        <div className="min-w-0 flex-1">
          <h2 className="text-base font-semibold text-green-900 dark:text-green-200">The service desk marked this as resolved</h2>
          <p className="mt-1 text-sm text-green-800 dark:text-green-300">If everything is working again, close the request. If not, reply below and it will be reopened by an agent.</p>
        </div>
        <Button className="h-11 sm:ml-auto" onClick={() => setConfirmClose(true)}>Confirm and close</Button>
      </div>
    </section>}

    {isClosed && <p className="rounded-xl border border-slate-200 bg-white p-5 text-sm text-slate-500 dark:border-slate-800 dark:bg-slate-900">This request is closed. Reply below if you need to reopen the conversation with the service desk.</p>}

    <section className="rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
      <h2 className="text-base font-semibold">What you told us</h2>
      <p className="mt-3 whitespace-pre-wrap text-[15px] leading-7 text-slate-600 dark:text-slate-300">{item.description}</p>
    </section>

    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="border-b border-slate-200 p-6 dark:border-slate-800">
        <h2 className="text-base font-semibold">Conversation</h2>
        <p className="mt-1 text-sm text-slate-500">Replies between you and the service desk.</p>
      </div>
      <ol className="divide-y divide-slate-200 dark:divide-slate-800">
        {thread.length === 0
          ? <li className="p-10 text-center"><span className="mx-auto grid size-12 place-items-center rounded-full bg-slate-100 text-slate-500 dark:bg-slate-800"><MessageSquare size={22} /></span><p className="mt-3 text-sm text-slate-500">No replies yet. The service desk will be in touch.</p></li>
          : thread.map((comment) => <li key={comment.id} className="p-6">
              <div className="flex flex-wrap items-baseline gap-2">
                <p className="text-sm font-semibold">{comment.authorName}</p>
                <p className="text-xs text-slate-500">{formatLocal(comment.createdAt)}</p>
              </div>
              <p className="mt-2 whitespace-pre-wrap text-[15px] leading-7 text-slate-600 dark:text-slate-300">{comment.body}</p>
            </li>)}
      </ol>
      <form className="border-t border-slate-200 p-6 dark:border-slate-800" onSubmit={(event) => { event.preventDefault(); if (reply.trim()) addComment.mutate() }}>
        <label className="block">
          <span className="block text-sm font-medium text-slate-700 dark:text-slate-200">Add a reply</span>
          <textarea aria-label="Add a reply" className="input mt-2 min-h-28 resize-y" placeholder="Share an update or answer a question from the service desk…" value={reply} onChange={(event) => setReply(event.target.value)} />
        </label>
        <div className="mt-3 flex flex-wrap items-center gap-3">
          {addComment.error && <span role="alert" className="text-sm text-red-600">{addComment.error.message}</span>}
          <Button className="ml-auto h-11" type="submit" disabled={!reply.trim() || addComment.isPending}><Send size={17} />{addComment.isPending ? 'Sending…' : 'Send reply'}</Button>
        </div>
      </form>
    </section>

    {confirmClose && <div className="fixed inset-0 z-50 grid place-items-center bg-slate-950/50 p-4" role="presentation" onMouseDown={(event) => { if (event.target === event.currentTarget && !close.isPending) setConfirmClose(false) }}>
      <section role="dialog" aria-modal="true" aria-labelledby="close-request-title" className="w-full max-w-md rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900">
        <h2 id="close-request-title" className="text-lg font-semibold">Close this request?</h2>
        <p className="mt-2 text-sm text-slate-500">Closing #{item.number} tells the service desk the problem is fixed. You can still reply afterwards if it comes back.</p>
        {close.error && <p role="alert" className="mt-3 text-sm text-red-600">{close.error.message}</p>}
        <div className="mt-6 flex justify-end gap-3">
          <Button type="button" variant="secondary" disabled={close.isPending} onClick={() => setConfirmClose(false)}>Not yet</Button>
          <Button type="button" disabled={close.isPending} onClick={() => close.mutate()}>{close.isPending ? 'Closing…' : 'Yes, close it'}</Button>
        </div>
      </section>
    </div>}
  </div>
}
