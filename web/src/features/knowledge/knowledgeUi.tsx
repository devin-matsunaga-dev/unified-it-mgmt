import type { ReactNode } from 'react'
import { cn } from '../../lib/utils'
import type { KbArticleStatus } from '../../api/knowledge'

/** The lifecycle in order, which is what the filter chips read. */
export const kbStatuses: KbArticleStatus[] = ['Draft', 'Published', 'Archived']

/**
 * DESIGN §3's semantic families: live is green, unfinished is amber, out of use is neutral.
 *
 * The tones matter more here than on most boards, because "is this published?" is the only question that
 * decides whether anybody outside the service desk can see it.
 */
export function KbStatusPill({ status }: { status: KbArticleStatus }) {
  return <span className={cn(
    'inline-flex rounded-md px-2 py-0.5 text-xs font-medium',
    status === 'Draft' && 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300',
    status === 'Published' && 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300',
    status === 'Archived' && 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300',
  )}>{status}</span>
}

/** The tone alone, for the global search dropdown's shared pill. */
export const kbStatusTone = (status: string) =>
  status === 'Published'
    ? 'bg-green-100 text-green-700 dark:bg-green-950 dark:text-green-300'
    : status === 'Draft'
      ? 'bg-amber-100 text-amber-700 dark:bg-amber-950 dark:text-amber-300'
      : 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300'

/** What each transition button says, which is the verb rather than the state it lands in. */
export const kbTransitionLabel = (status: KbArticleStatus) =>
  status === 'Published' ? 'Publish' : status === 'Draft' ? 'Back to draft' : 'Archive'

type Block =
  | { kind: 'heading'; level: 2 | 3; text: string }
  | { kind: 'paragraph'; text: string }
  | { kind: 'list'; ordered: boolean; items: string[] }

/**
 * Article bodies are written as Markdown, and this turns the handful of shapes they actually use into
 * React nodes: headings, bullet and numbered lists, paragraphs, and `**bold**` within them.
 *
 * Deliberately hand-rolled and deliberately small. A Markdown library is a dependency this package was not
 * asked to add, and the one thing that must never happen to an article body is that it reaches the page as
 * HTML — everything here becomes React elements, so a body containing `<script>` renders as the text
 * `<script>` and nothing else. Anything this parser does not recognise falls through as a paragraph, which
 * is exactly what the previous behaviour (`whitespace-pre-wrap`) already was.
 */
export function parseArticleBody(body: string): Block[] {
  const blocks: Block[] = []
  let paragraph: string[] = []
  let list: { ordered: boolean; items: string[] } | null = null

  const flushParagraph = () => {
    if (paragraph.length > 0) {
      blocks.push({ kind: 'paragraph', text: paragraph.join(' ') })
      paragraph = []
    }
  }
  const flushList = () => {
    if (list) {
      blocks.push({ kind: 'list', ordered: list.ordered, items: list.items })
      list = null
    }
  }

  for (const rawLine of body.replaceAll('\r\n', '\n').split('\n')) {
    const line = rawLine.trim()
    if (line.length === 0) {
      flushParagraph()
      flushList()
      continue
    }

    const heading = /^(#{2,3})\s+(.*)$/.exec(line)
    if (heading) {
      flushParagraph()
      flushList()
      blocks.push({ kind: 'heading', level: heading[1].length === 2 ? 2 : 3, text: heading[2] })
      continue
    }

    const bullet = /^[-*]\s+(.*)$/.exec(line)
    const ordered = /^\d+[.)]\s+(.*)$/.exec(line)
    if (bullet || ordered) {
      flushParagraph()
      const isOrdered = ordered !== null
      const text = (bullet ?? ordered)![1]
      if (list && list.ordered === isOrdered) list.items.push(text)
      else {
        flushList()
        list = { ordered: isOrdered, items: [text] }
      }
      continue
    }

    // A continuation line inside a list item, which is how the seeded articles wrap.
    if (list) {
      list.items[list.items.length - 1] += ` ${line}`
      continue
    }

    paragraph.push(line)
  }

  flushParagraph()
  flushList()
  return blocks
}

export function ArticleBody({ body, className }: { body: string; className?: string }) {
  const blocks = parseArticleBody(body)
  return <div className={cn('space-y-3 text-sm leading-6 text-slate-600 dark:text-slate-300', className)}>
    {blocks.map((block, index) => {
      if (block.kind === 'heading') {
        return block.level === 2
          ? <h3 key={index} className="pt-2 text-base font-semibold text-slate-900 dark:text-slate-100">{inline(block.text)}</h3>
          : <h4 key={index} className="pt-1 text-sm font-semibold text-slate-900 dark:text-slate-100">{inline(block.text)}</h4>
      }

      if (block.kind === 'list') {
        return block.ordered
          ? <ol key={index} className="ml-5 list-decimal space-y-1">{block.items.map((item, itemIndex) => <li key={itemIndex}>{inline(item)}</li>)}</ol>
          : <ul key={index} className="ml-5 list-disc space-y-1">{block.items.map((item, itemIndex) => <li key={itemIndex}>{inline(item)}</li>)}</ul>
      }

      return <p key={index}>{inline(block.text)}</p>
    })}
  </div>
}

/**
 * `**bold**` and `*italic*`, as React nodes. Split rather than replaced: the pieces are text nodes, so
 * nothing in an article body can become markup however it is written.
 */
export function inline(text: string): ReactNode[] {
  return text.split(/(\*\*[^*]+\*\*|\*[^*]+\*)/g)
    .filter((part) => part.length > 0)
    .map((part, index) => {
      if (part.startsWith('**') && part.endsWith('**') && part.length > 4) {
        return <strong key={index} className="font-semibold text-slate-900 dark:text-slate-100">{part.slice(2, -2)}</strong>
      }
      if (part.startsWith('*') && part.endsWith('*') && part.length > 2) {
        return <em key={index}>{part.slice(1, -1)}</em>
      }
      return <span key={index}>{part}</span>
    })
}
