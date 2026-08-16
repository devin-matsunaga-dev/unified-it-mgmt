import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { Search, SearchX } from 'lucide-react'
import { useEffect, useId, useRef, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { searchApi, type SearchHit } from '../../api/search'
import { ciLifecycleLabel, ciLifecycleTone } from '../assets/lifecycle'
import { cn } from '../../lib/utils'
import { severityTone } from '../monitoring/severity'
import { displayStatus } from '../tickets/ticketUi'
import {
  describeTruncation,
  flattenHits,
  groupIcons,
  hitKey,
  minimumTermLength,
  moveHighlight,
  searchGroupLabel,
  searchResultHref,
  visibleGroups,
} from './searchUi'

/**
 * The global search bar (WP-5.4): one box over tickets, assets, devices, alerts and people, with the
 * results grouped by kind and walkable from the keyboard.
 *
 * Everything about *what* matched is the server's: five sources are queried, ranked and capped separately,
 * and this renders what comes back in the order it comes back. What is decided here is only how a reader
 * moves through it.
 */
export function GlobalSearch() {
  const navigate = useNavigate()
  const [term, setTerm] = useState('')
  const [debounced, setDebounced] = useState('')
  const [open, setOpen] = useState(false)
  const [highlight, setHighlight] = useState(-1)
  const containerRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const listId = useId()

  // Debounced into the query rather than sent per keystroke: this is five indexed reads against one
  // database, and a search box that fires all five on every letter is a worse neighbour than one that waits.
  useEffect(() => {
    const timer = window.setTimeout(() => setDebounced(term.trim()), 200)
    return () => window.clearTimeout(timer)
  }, [term])

  // Below two characters nothing is sent at all. A one-letter prefix matches most of the estate, so it
  // would cost five wide reads to return a list nobody wants.
  const enabled = debounced.length >= minimumTermLength
  const results = useQuery({
    queryKey: ['search', debounced],
    queryFn: () => searchApi.search(debounced),
    enabled,
    // The previous results stay on screen while the next ones load. A list that empties and refills on
    // every keystroke reads as a box that keeps losing what it found.
    placeholderData: keepPreviousData,
  })

  const hits = results.data ? flattenHits(results.data) : []
  const groups = results.data ? visibleGroups(results.data) : []
  const truncation = results.data ? describeTruncation(results.data) : null

  // A new set of results invalidates wherever the highlight was: index 3 of the old list is a different
  // record in the new one, and Enter would open something the reader never looked at.
  useEffect(() => setHighlight(-1), [debounced])

  useEffect(() => {
    if (!open) return
    const closeOnOutsideClick = (event: MouseEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) setOpen(false)
    }
    document.addEventListener('mousedown', closeOnOutsideClick)
    return () => document.removeEventListener('mousedown', closeOnOutsideClick)
  }, [open])

  // Ctrl/Cmd+K from anywhere, the shortcut every search box in every tool this replaces already uses. Not a
  // bare "/", which would swallow the slash somebody is typing into a form field.
  useEffect(() => {
    const focusOnShortcut = (event: KeyboardEvent) => {
      if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
        event.preventDefault()
        inputRef.current?.focus()
        inputRef.current?.select()
      }
    }
    document.addEventListener('keydown', focusOnShortcut)
    return () => document.removeEventListener('keydown', focusOnShortcut)
  }, [])

  function go(hit: SearchHit) {
    setOpen(false)
    setTerm('')
    setDebounced('')
    inputRef.current?.blur()
    navigate(searchResultHref(hit))
  }

  function onKeyDown(event: React.KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Escape') {
      setOpen(false)
      return
    }

    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      setOpen(true)
      setHighlight((current) => moveHighlight(current, event.key === 'ArrowDown' ? 1 : -1, hits.length))
      return
    }

    // Enter with nothing highlighted deliberately does nothing rather than opening the first result: the
    // reader has not chosen anything, and guessing for them is how somebody lands on a record they never saw.
    if (event.key === 'Enter' && highlight >= 0 && hits[highlight]) {
      event.preventDefault()
      go(hits[highlight])
    }
  }

  const showPanel = open && enabled
  const activeHit = highlight >= 0 ? hits[highlight] : undefined

  return <div ref={containerRef} className="relative ml-auto hidden max-w-sm flex-1 md:block">
    <div className="flex h-10 items-center gap-2 rounded-lg border border-slate-200 px-3 text-slate-500 focus-within:border-blue-600 focus-within:ring-2 focus-within:ring-blue-600/20 dark:border-slate-700">
      <Search size={18} aria-hidden />
      <input
        ref={inputRef}
        role="combobox"
        aria-label="Global search"
        aria-expanded={showPanel}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={activeHit ? `${listId}-${hitKey(activeHit)}` : undefined}
        className="w-full bg-transparent text-sm outline-none"
        placeholder="Search tickets, assets, alerts..."
        value={term}
        onChange={(event) => { setTerm(event.target.value); setOpen(true) }}
        onFocus={() => setOpen(true)}
        onKeyDown={onKeyDown} />
    </div>

    {showPanel && <div className="absolute right-0 top-[calc(100%+8px)] z-40 w-[420px] max-w-[calc(100vw-2rem)] overflow-hidden rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-700 dark:bg-slate-900">
      {results.isLoading
        ? <div aria-label="Searching" className="space-y-2 p-4">
            {Array.from({ length: 3 }, (_, index) => <div key={index}
              className="h-10 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />)}
          </div>
        // A failed read is a fact about the request; nothing found is a claim about the estate. The two must
        // not read the same (the WP-2.11 rule).
        : results.isError || !results.data
          ? <p role="alert" className="p-4 text-sm text-red-600">The search could not be run.</p>
          : hits.length === 0
            ? <Nothing term={results.data.term} />
            : <>
                <ul role="listbox" id={listId} aria-label="Search results"
                  className="max-h-[60vh] overflow-y-auto py-1">
                  {groups.map((group) => {
                    const Icon = groupIcons[group.type]
                    // `group` and not `presentation`: a listbox may own options and groups, and a group
                    // keeps the heading attached to the rows under it when a screen reader announces them.
                    return <li key={group.type} role="group" aria-label={searchGroupLabel(group.type)}>
                      <p aria-hidden className="flex items-center gap-1.5 px-3 pb-1 pt-2 text-[11px] font-medium uppercase tracking-wide text-slate-500">
                        <Icon size={12} aria-hidden />
                        {searchGroupLabel(group.type)}
                        {/* The honest total beside the heading, so five of ninety says ninety. */}
                        {group.truncated && <span className="font-normal normal-case tracking-normal">
                          — showing {group.returned} of {group.total}
                        </span>}
                      </p>
                      <ul role="presentation">
                        {group.hits.map((hit) => <Row key={hitKey(hit)}
                          id={`${listId}-${hitKey(hit)}`}
                          hit={hit}
                          active={activeHit !== undefined && hitKey(activeHit) === hitKey(hit)}
                          onSelect={() => go(hit)}
                          onHover={() => setHighlight(hits.findIndex((item) => hitKey(item) === hitKey(hit)))} />)}
                      </ul>
                    </li>
                  })}
                </ul>
                {truncation && <p className="border-t border-slate-200 px-3 py-2 text-xs text-slate-500 dark:border-slate-800">
                  {truncation}
                </p>}
              </>}
    </div>}
  </div>
}

function Row({ id, hit, active, onSelect, onHover }: {
  id: string
  hit: SearchHit
  active: boolean
  onSelect: () => void
  onHover: () => void
}) {
  return <li
    id={id}
    role="option"
    aria-selected={active}
    onMouseEnter={onHover}
    // mousedown rather than click: the input's blur would otherwise close the panel before the click lands.
    onMouseDown={(event) => { event.preventDefault(); onSelect() }}
    className={cn('cursor-pointer px-3 py-2', active ? 'bg-blue-50 dark:bg-slate-800' : 'hover:bg-slate-50 dark:hover:bg-slate-800/60')}>
    <div className="flex items-baseline gap-2">
      <span className="min-w-0 flex-1 truncate text-sm font-medium text-slate-900 dark:text-slate-100">
        {hit.title}
      </span>
      {hit.badge && <Badge type={hit.type} badge={hit.badge} />}
    </div>
    <p className="mt-0.5 flex gap-2 text-xs text-slate-500">
      {hit.reference && <span className="shrink-0 font-mono">{hit.reference}</span>}
      {hit.subtitle && <span className="min-w-0 truncate">{hit.subtitle}</span>}
    </p>
  </li>
}

/**
 * One pill, coloured by whichever vocabulary the kind belongs to. The badge arrives as a raw token so that
 * each kind is labelled and coloured by the map the rest of the app already uses for it — a second spelling
 * composed on the server would be a copy of five vocabularies to keep in step.
 */
function Badge({ type, badge }: { type: SearchHit['type']; badge: string }) {
  const tone = type === 'Alert'
    ? severityTone[badge as keyof typeof severityTone] ?? severityTone.Ok
    : type === 'Ci'
      ? ciLifecycleTone(badge)
      : 'bg-slate-100 text-slate-600 dark:bg-slate-800 dark:text-slate-300'
  const label = type === 'Ci' ? ciLifecycleLabel(badge) : displayStatus(badge)
  return <span className={cn('shrink-0 rounded-md px-1.5 py-0.5 text-[11px] font-medium', tone)}>{label}</span>
}

/**
 * Not a bare "No results" (DESIGN §6). It names the term back, so a reader can see the typo, and it says
 * what was searched — because "nothing in the platform matches this" and "this box only looks at tickets"
 * are the two things somebody staring at an empty dropdown is trying to decide between.
 */
function Nothing({ term }: { term: string }) {
  return <div className="grid place-items-center px-4 py-8 text-center">
    <div>
      <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15">
        <SearchX />
      </span>
      <p className="mt-3 text-sm font-medium">Nothing matches “{term}”</p>
      <p className="mt-1 text-xs text-slate-500">
        Tickets, assets, devices, alerts and people were all searched.
      </p>
    </div>
  </div>
}
