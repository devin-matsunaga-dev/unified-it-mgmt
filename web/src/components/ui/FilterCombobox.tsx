import { Check, ChevronDown, X } from 'lucide-react'
import { useEffect, useId, useMemo, useRef, useState } from 'react'
import { cn } from '../../lib/utils'

export type ComboboxOption = { value: string; label: string }

/**
 * A filter that can be typed into as well as chosen from.
 *
 * A native select cannot be searched, and a list of people is exactly where that stops being
 * workable: an estate of two hundred staff is a two-hundred-row menu somebody has to scroll. This
 * keeps the shape and weight of the selects beside it — DESIGN.md §6 — and adds typing.
 *
 * The keyboard contract follows the one WP-5.4's global search already established, so both boxes in
 * this app behave the same way: arrows move, Enter takes the highlighted row, Escape closes without
 * changing anything, and Enter with nothing highlighted does nothing rather than guessing.
 */
export function FilterCombobox({ label, value, options, onChange, emptyLabel, className }: {
  /** The accessible name. Matches the aria-label the equivalent select would have carried. */
  label: string
  /** The selected option's value, or null for "no filter". */
  value: string | null
  options: ComboboxOption[]
  onChange: (value: string | null) => void
  /** What "no filter" reads as — "All owners". Always offered, so a filter can be cleared. */
  emptyLabel: string
  className?: string
}) {
  const [open, setOpen] = useState(false)
  const [term, setTerm] = useState('')
  const [highlight, setHighlight] = useState(-1)
  const containerRef = useRef<HTMLDivElement>(null)
  const inputRef = useRef<HTMLInputElement>(null)
  const listId = useId()

  const selected = options.find((option) => option.value === value) ?? null

  /**
   * Matching is case-insensitive and anywhere in the label, not a prefix: people search for a surname
   * far more often than for the way a display name happens to start.
   *
   * The label is the only thing matched, deliberately. Anything matched but not shown would return
   * rows with no visible reason for being there.
   */
  const matches = useMemo(() => {
    const needle = term.trim().toLowerCase()
    if (needle === '') return options
    return options.filter((option) => option.label.toLowerCase().includes(needle))
  }, [options, term])

  // The clear row sits at index 0 so arrowing up from the first person reaches it.
  const rows: ComboboxOption[] = [{ value: '', label: emptyLabel }, ...matches]

  useEffect(() => {
    if (!open) return
    const closeOnOutsideClick = (event: MouseEvent) => {
      if (!containerRef.current?.contains(event.target as Node)) close()
    }

    document.addEventListener('mousedown', closeOnOutsideClick)
    return () => document.removeEventListener('mousedown', closeOnOutsideClick)
  }, [open])

  function close() {
    setOpen(false)
    setTerm('')
    setHighlight(-1)
  }

  function choose(option: ComboboxOption) {
    onChange(option.value === '' ? null : option.value)
    close()
    inputRef.current?.blur()
  }

  function onKeyDown(event: React.KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Escape') {
      close()
      return
    }

    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      setOpen(true)
      setHighlight((current) => {
        if (rows.length === 0) return -1
        const next = current + (event.key === 'ArrowDown' ? 1 : -1)
        return next < 0 ? rows.length - 1 : next >= rows.length ? 0 : next
      })
      return
    }

    // Enter with nothing highlighted does nothing rather than taking the first match: the reader has
    // not chosen anything, and guessing is how somebody filters by a person they never saw.
    if (event.key === 'Enter' && highlight >= 0 && rows[highlight]) {
      event.preventDefault()
      choose(rows[highlight])
    }
  }

  const active = highlight >= 0 ? rows[highlight] : undefined

  return <div ref={containerRef} className={cn('relative', className)}>
    <div className={cn('flex h-10 items-center gap-1 rounded-lg border border-slate-200 bg-white px-3 text-sm dark:border-slate-700 dark:bg-slate-900',
      open && 'border-blue-600 ring-2 ring-blue-600/20')}>
      <input
        ref={inputRef}
        role="combobox"
        aria-label={label}
        aria-expanded={open}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={active ? `${listId}-${active.value || 'none'}` : undefined}
        className="w-full min-w-0 bg-transparent text-slate-900 outline-none placeholder:text-slate-500 dark:text-slate-100"
        // Closed, it reads as the current filter; open, it is empty and waiting to be typed into, with
        // the selection kept as the placeholder so nothing about the current state is lost.
        placeholder={selected?.label ?? emptyLabel}
        value={open ? term : selected?.label ?? ''}
        onChange={(event) => { setTerm(event.target.value); setOpen(true); setHighlight(-1) }}
        onFocus={() => setOpen(true)}
        onKeyDown={onKeyDown} />

      {selected && !open && <button type="button" aria-label={`Clear ${label}`}
        className="shrink-0 rounded p-0.5 text-slate-400 hover:text-slate-600 dark:hover:text-slate-200"
        onClick={() => onChange(null)}>
        <X size={14} />
      </button>}
      <ChevronDown size={16} aria-hidden className="shrink-0 text-slate-400" />
    </div>

    {open && <ul
      role="listbox"
      id={listId}
      aria-label={label}
      className="absolute left-0 top-[calc(100%+4px)] z-40 max-h-64 w-full min-w-56 overflow-y-auto rounded-lg border border-slate-200 bg-white py-1 shadow-sm dark:border-slate-700 dark:bg-slate-900">
      {rows.length === 1 && matches.length === 0 && term.trim() !== ''
        ? <li className="px-3 py-2 text-[13px] text-slate-500">Nothing matches “{term.trim()}”</li>
        : null}
      {rows.map((option, index) => <li
        key={option.value || 'none'}
        id={`${listId}-${option.value || 'none'}`}
        role="option"
        aria-selected={(value ?? '') === option.value}
        onMouseEnter={() => setHighlight(index)}
        // mousedown, not click: the input's blur would otherwise close the list before the click lands.
        onMouseDown={(event) => { event.preventDefault(); choose(option) }}
        className={cn('flex cursor-pointer items-center gap-2 px-3 py-1.5 text-sm text-slate-700 dark:text-slate-200',
          index === highlight && 'bg-slate-100 dark:bg-slate-800',
          option.value === '' && 'text-slate-500')}>
        <Check size={14} aria-hidden
          className={cn('shrink-0', (value ?? '') === option.value ? 'text-blue-600' : 'invisible')} />
        <span className="min-w-0 flex-1 truncate">{option.label}</span>
      </li>)}
    </ul>}
  </div>
}
