import { useMutation } from '@tanstack/react-query'
import { BookOpen, Copy, FilePlus2 } from 'lucide-react'
import { useNavigate } from 'react-router-dom'
import { toast } from 'sonner'
import { knowledgeApi } from '../../api/knowledge'
import type { KnowledgeDraft } from '../../api/problems'
import { Button } from '../../components/ui/Button'

/**
 * The draft article somebody is prompted with when they close a problem.
 *
 * The prompt exists for the moment: the person closing the problem has just finished writing every field
 * the article needs, and asking them again next week gets a worse answer or none. WP-5.7 could only offer
 * the text; WP-5.9 built the knowledge base, so the button now writes a real draft article and records
 * which problem it came from.
 */
export function KnowledgeDraftDialog({ draft, onClose }: { draft: KnowledgeDraft; onClose: () => void }) {
  const navigate = useNavigate()
  const markdown = draftAsMarkdown(draft)

  const create = useMutation({
    mutationFn: () => knowledgeApi.create({
      title: draft.title,
      // A summary the article can be published with, from what the problem already said. The workaround
      // first, because that is what somebody arriving from an incident needs; the cause is why.
      summary: (draft.workaround ?? draft.rootCause ?? `Written from ${draft.problemNumber}.`).slice(0, 500),
      body: markdown,
      problemId: draft.problemId,
    }),
    onSuccess: (article) => {
      toast.success(`${article.number} created as a draft`)
      navigate(`/knowledge/${article.id}`)
    },
  })

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(markdown)
      toast.success('Draft copied')
    } catch {
      // A denied clipboard permission is not an error worth a red toast — the text is on screen and
      // selectable, which is the fallback every browser still allows.
      toast.message('Copying was blocked. Select the text below instead.')
    }
  }

  return <div role="dialog" aria-modal="true" aria-label="Knowledge article draft" className="fixed inset-0 z-50 grid place-items-center bg-slate-950/50 p-4">
    <div className="flex max-h-[85vh] w-full max-w-2xl flex-col rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="flex items-center gap-3 border-b border-slate-200 p-5 dark:border-slate-800">
        <span className="grid size-10 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><BookOpen size={20} /></span>
        <div>
          <h2 className="font-semibold">Write this up while it is fresh</h2>
          <p className="mt-0.5 text-sm text-slate-500">
            {draft.problemNumber} is closed. Here is the article it would make, filled in from what you
            already recorded.
          </p>
        </div>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto p-5">
        <Field label="Title" value={draft.title} />
        {draft.subjectName && <Field label="About" value={draft.subjectName} />}
        <div className="mt-4">
          <h3 className="text-[13px] font-medium text-slate-500">Symptoms people reported</h3>
          {draft.symptoms.length === 0
            ? <p className="mt-1 text-sm text-slate-500">No incidents were linked to this problem.</p>
            : <ul className="mt-2 space-y-1 text-sm text-slate-600 dark:text-slate-300">
                {draft.symptoms.map((symptom) => <li key={symptom.text} className="flex gap-2">
                  <span className="flex-1">{symptom.text}</span>
                  <span className="text-xs text-slate-500">reported {symptom.incidentCount}×</span>
                </li>)}
              </ul>}
        </div>
        <Field label="Root cause" value={draft.rootCause} />
        <Field label="Workaround" value={draft.workaround} />
        <Field label="Resolution" value={draft.resolution} />
        {draft.incidentNumbers.length > 0 && <Field label="Incidents" value={draft.incidentNumbers.join(', ')} />}
      </div>

      <div className="flex flex-wrap items-center gap-2 border-t border-slate-200 p-4 dark:border-slate-800">
        <p className="mr-auto text-xs text-slate-500">
          It is created as a draft — nobody outside the service desk sees it until you publish it.
        </p>
        {create.error && <p role="alert" className="w-full text-sm text-red-600">{create.error.message}</p>}
        <Button variant="secondary" onClick={() => void copy()}><Copy size={16} />Copy as Markdown</Button>
        <Button variant="secondary" onClick={onClose}>Not now</Button>
        <Button disabled={create.isPending} onClick={() => create.mutate()}>
          <FilePlus2 size={16} />{create.isPending ? 'Creating…' : 'Create article'}
        </Button>
      </div>
    </div>
  </div>
}

/**
 * A field nobody filled in says so rather than showing a blank line, because the prompt's whole job is to
 * make what is missing obvious.
 */
function Field({ label, value }: { label: string; value: string | null }) {
  return <div className="mt-4">
    <h3 className="text-[13px] font-medium text-slate-500">{label}</h3>
    {value
      ? <p className="mt-1 whitespace-pre-wrap text-sm leading-6 text-slate-600 dark:text-slate-300">{value}</p>
      : <p className="mt-1 text-sm italic text-amber-700 dark:text-amber-500">Not recorded — the article will need this.</p>}
  </div>
}

/** Markdown rather than plain text, because wherever this is pasted the headings are what make it readable. */
export function draftAsMarkdown(draft: KnowledgeDraft) {
  const sections = [
    `# ${draft.title}`,
    draft.subjectName ? `**About:** ${draft.subjectName}` : null,
    draft.symptoms.length > 0
      ? `## Symptoms\n${draft.symptoms.map((symptom) => `- ${symptom.text} (reported ${symptom.incidentCount}×)`).join('\n')}`
      : null,
    draft.rootCause ? `## Root cause\n${draft.rootCause}` : null,
    draft.workaround ? `## Workaround\n${draft.workaround}` : null,
    draft.resolution ? `## Resolution\n${draft.resolution}` : null,
    draft.incidentNumbers.length > 0 ? `## Incidents\n${draft.incidentNumbers.join(', ')}` : null,
    `_Written from ${draft.problemNumber}._`,
  ]
  return sections.filter((section) => section !== null).join('\n\n')
}
