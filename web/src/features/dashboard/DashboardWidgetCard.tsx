import { Check, ChevronDown, ChevronRight, GripVertical, Trash2, TriangleAlert } from 'lucide-react'
import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import type {
  DashboardDisplay,
  DashboardPlacement,
  DashboardRow,
  DashboardWidget,
  DashboardWidgetType,
  DashboardWidgetWidth,
} from '../../api/dashboard'
import { cn } from '../../lib/utils'
import { DashboardWidgetChart } from './DashboardWidgetChart'
import {
  dashboardHref,
  dashboardLinkLabel,
  describeRowTruncation,
  displayLabel,
  displays,
  effectiveDisplay,
  formatLocal,
  segmentShares,
  supportsChart,
  toneDot,
  tonePill,
  toneText,
  toneTint,
  widgetIcon,
  widthLabel,
  widths,
} from './dashboardUi'

export type WidgetCardControls = {
  index: number
  count: number
  /** Every widget this person could put here, placed or not — the title menu's whole list. */
  choices: DashboardWidget[]
  placedTypes: DashboardWidgetType[]
  onMove: (to: number) => void
  onReplace: (type: DashboardWidgetType) => void
  onWidth: (width: DashboardWidgetWidth) => void
  onDisplay: (display: DashboardDisplay) => void
  onRemove: () => void
  onDragStart: () => void
  onDragOverCard: () => void
  onDropOn: () => void
  onDragEnd: () => void
}

/**
 * One widget, drawn from the one shape every widget arrives in (WP-5.5): a headline, a set of counted
 * bands, and some named rows. A widget that uses only one of the three simply sends the others empty, and a
 * widget this app has never heard of still renders — which is what makes adding one on the server a
 * registration rather than a release of both halves.
 */
export function DashboardWidgetCard({ widget, placement, arranging, dragging, dropTarget, controls }: {
  widget: DashboardWidget
  placement: DashboardPlacement
  arranging: boolean
  dragging: boolean
  dropTarget: boolean
  controls: WidgetCardControls
}) {
  const href = dashboardHref(widget.link)
  const truncation = describeRowTruncation(widget)
  const shares = segmentShares(widget.segments)
  const Icon = widgetIcon(widget.type)
  // A view saved when a widget had bands must not break when it stops having them, so the shape asked for
  // is only the shape drawn while the payload can still carry it.
  const display = effectiveDisplay(widget, placement.display)

  return <article
    aria-label={widget.title}
    // Draggable only while arranging, and — crucially — with every link inside the card made inert at the
    // same time. A card is full of anchors, anchors are natively draggable, and a grab that lands on one
    // starts a link drag instead of this one: that is why the first cut of this never moved a card.
    draggable={arranging}
    onDragStart={arranging ? (event) => {
      // Firefox refuses to start a drag at all without payload on the transfer, and Chrome needs the
      // effect set for the cursor to say "move" rather than "no".
      event.dataTransfer.setData('text/plain', widget.type)
      event.dataTransfer.effectAllowed = 'move'
      controls.onDragStart()
    } : undefined}
    onDragOver={arranging ? (event) => {
      // Without preventDefault on dragover the browser refuses the drop, silently.
      event.preventDefault()
      event.dataTransfer.dropEffect = 'move'
      controls.onDragOverCard()
    } : undefined}
    onDrop={arranging ? (event) => { event.preventDefault(); controls.onDropOn() } : undefined}
    onDragEnd={arranging ? controls.onDragEnd : undefined}
    className={cn('flex h-full flex-col rounded-xl border bg-white p-5 transition-shadow dark:bg-slate-900',
      arranging
        ? 'cursor-grab border-dashed border-slate-300 dark:border-slate-600'
        : 'border-slate-200 dark:border-slate-800',
      dragging && 'opacity-40',
      // Where it will land if it is dropped now. Without it a drag is a guess: the card being dragged is
      // under the pointer and the one that will move is not marked at all.
      dropTarget && !dragging && 'border-solid border-blue-600 ring-2 ring-blue-600/30')}>

    <header className="mb-4 flex items-start gap-3">
      {arranging && <GripVertical size={18} aria-hidden className="mt-2 shrink-0 text-slate-400" />}
      <span className={cn('mt-0.5 grid size-9 shrink-0 place-items-center rounded-full',
        toneTint[widget.headlineTone])}>
        <Icon size={18} aria-hidden />
      </span>
      <div className="min-w-0 flex-1">
        <CardMenu widget={widget} placement={placement} controls={controls} />
        {widget.subtitle && <p className="mt-0.5 text-[13px] text-slate-500">{widget.subtitle}</p>}
      </div>
      {!arranging && href && <Link to={href}
        className="mt-1 shrink-0 text-[13px] font-medium text-blue-600 hover:underline">
        {dashboardLinkLabel(widget.link)}
      </Link>}
    </header>

    {/* A failed widget says so. Drawing zeroes would make an unreachable table read as an estate with
        nothing in it — the rule this repo has applied to KPI counts, status boards and drift reports. */}
    {widget.status === 'Failed'
      ? <p role="status" className="flex flex-1 items-center gap-2 rounded-lg bg-slate-50 px-3 py-6 text-sm text-slate-500 dark:bg-slate-800/50">
          <TriangleAlert size={18} className="shrink-0 text-amber-600" aria-hidden />
          This widget could not be loaded. The rest of the dashboard is unaffected.
        </p>
      // While arranging, everything below the header stops taking clicks: a card being dragged must not
      // navigate when the pointer is released, and a drag that begins on an anchor is not a card drag.
      : display !== 'Card'
        ? <div className={cn('flex flex-1 flex-col', arranging && 'pointer-events-none select-none')}>
            <DashboardWidgetChart
              display={display}
              segments={widget.segments}
              // The headline where the widget gave one, so the middle of a donut says what the card would
              // have said; otherwise the bands add up to their own total.
              total={widget.headline ?? widget.segments.reduce((sum, segment) => sum + segment.value, 0)}
              totalLabel={widget.headlineLabel}
              title={widget.title} />
          </div>
      : <div className={cn('flex flex-1 flex-col', arranging && 'pointer-events-none select-none')}>
          {widget.headline !== null && <div className="mb-4 flex items-end justify-between gap-3">
            <p>
              <span className={cn('block text-[30px] font-bold leading-none tabular-nums',
                toneText[widget.headlineTone])}>
                {widget.headline}
              </span>
              {widget.headlineLabel && <span className="mt-1.5 block text-[13px] text-slate-500">
                {widget.headlineLabel}
              </span>}
            </p>
          </div>}

          {/* The proportions at a glance, before any of the numbers are read. Hidden from screen readers:
              the same information is in the list underneath, where it can actually be heard. */}
          {shares && <div aria-hidden className="mb-4 flex h-2 gap-0.5 overflow-hidden rounded-full">
            {shares.map((share) => <span key={share.label}
              className={cn('h-full', toneDot[share.tone])}
              style={{ width: `${share.percent}%` }} />)}
          </div>}

          {widget.segments.length > 0 && <ul className="grid gap-0.5">
            {widget.segments.map((segment) => {
              const to = dashboardHref(segment.link)
              const body = <>
                <span className={cn('size-2 shrink-0 rounded-full', toneDot[segment.tone])} aria-hidden />
                <span className="min-w-0 flex-1 truncate text-sm text-slate-600 dark:text-slate-300">
                  {segment.label}
                </span>
                <span className={cn('text-sm font-semibold tabular-nums', toneText[segment.tone])}>
                  {segment.value}
                </span>
              </>
              return <li key={segment.label}>
                {/* Every band is a deep link into the list it counts — the WP's third verification step. */}
                {to
                  ? <Link to={to} draggable={false}
                      className="-mx-2 flex items-center gap-2.5 rounded-lg px-2 py-1.5 hover:bg-slate-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:hover:bg-slate-800/60">
                      {body}
                    </Link>
                  : <span className="-mx-2 flex items-center gap-2.5 px-2 py-1.5">{body}</span>}
              </li>
            })}
          </ul>}

          {widget.rows.length > 0 && <ul className={cn('divide-y divide-slate-100 dark:divide-slate-800',
            widget.segments.length > 0 && 'mt-4 border-t border-slate-200 pt-1 dark:border-slate-800')}>
            {widget.rows.map((row) => <Row key={`${row.title}-${row.link?.recordId ?? ''}`} row={row} />)}
          </ul>}

          {widget.segments.length === 0 && widget.rows.length === 0 && widget.headline === null
            && <p className="flex flex-1 items-center justify-center py-6 text-sm text-slate-500">
              Nothing to show yet.
            </p>}

          {truncation && <p className="mt-3 text-xs text-slate-500">{truncation}</p>}
        </div>}
  </article>
}

/**
 * The card's title, and the menu behind it.
 *
 * Clicking a heading to change what the card shows is the quickest path there is between "this card is not
 * the one I want here" and a board that is right — quicker than removing a card and adding another, which
 * is the same intention expressed twice. Width lives here too, so a card can be reshaped without entering
 * a mode.
 */
function CardMenu({ widget, placement, controls }: {
  widget: DashboardWidget
  placement: DashboardPlacement
  controls: WidgetCardControls
}) {
  const [open, setOpen] = useState(false)
  const container = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const closeOnOutsideClick = (event: MouseEvent) => {
      if (!container.current?.contains(event.target as Node)) setOpen(false)
    }
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    document.addEventListener('mousedown', closeOnOutsideClick)
    document.addEventListener('keydown', closeOnEscape)
    return () => {
      document.removeEventListener('mousedown', closeOnOutsideClick)
      document.removeEventListener('keydown', closeOnEscape)
    }
  }, [open])

  const placed = new Set(controls.placedTypes)

  return <div ref={container} className="relative">
    <h2>
      <button type="button" aria-haspopup="menu" aria-expanded={open}
        onClick={() => setOpen((current) => !current)}
        className="-mx-1.5 flex items-center gap-1 rounded-md px-1.5 py-0.5 text-left font-semibold hover:bg-slate-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:hover:bg-slate-800">
        <span className="truncate">{widget.title}</span>
        <ChevronDown size={15} aria-hidden className="shrink-0 text-slate-400" />
      </button>
    </h2>

    {open && <div role="menu" aria-label={`${widget.title} options`}
      className="absolute left-0 top-[calc(100%+6px)] z-30 w-64 rounded-xl border border-slate-200 bg-white p-1.5 shadow-lg dark:border-slate-700 dark:bg-slate-900">
      <p className="px-2 py-1 text-[11px] font-medium uppercase tracking-wide text-slate-500">Show</p>
      {controls.choices.map((choice) => <button key={choice.type} type="button" role="menuitemradio"
        aria-checked={choice.type === widget.type}
        onClick={() => { controls.onReplace(choice.type); setOpen(false) }}
        className="flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-sm hover:bg-slate-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:hover:bg-slate-800">
        <Check size={15} aria-hidden
          className={cn('shrink-0 text-blue-600', choice.type !== widget.type && 'invisible')} />
        <span className="min-w-0 flex-1 truncate">{choice.title}</span>
        {/* A widget already on the board swaps places with this one rather than appearing twice. */}
        {choice.type !== widget.type && placed.has(choice.type)
          && <span className="shrink-0 text-[11px] text-slate-400">swap</span>}
      </button>)}

      <p className="mt-1 border-t border-slate-200 px-2 pb-1 pt-2 text-[11px] font-medium uppercase tracking-wide text-slate-500 dark:border-slate-800">
        Show as
      </p>
      <div className="flex gap-1 px-1 pb-1">
        {displays.map((option) => {
          // A widget with no bands has nothing to plot, so the chart shapes are offered but disabled
          // rather than hidden: a control that vanishes reads as a feature that broke.
          const unavailable = option !== 'Card' && !supportsChart(widget)
          return <button key={option} type="button" role="menuitemradio"
            aria-checked={option === placement.display}
            disabled={unavailable}
            title={unavailable ? 'This widget has no bands to chart.' : undefined}
            onClick={() => controls.onDisplay(option)}
            className={cn('flex-1 rounded-md border px-1 py-1 text-[11px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 disabled:opacity-40',
              option === placement.display
                ? 'border-blue-600 bg-blue-50 text-blue-700 dark:bg-blue-500/15 dark:text-blue-300'
                : 'border-slate-200 text-slate-600 hover:bg-slate-100 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800')}>
            {displayLabel[option]}
          </button>
        })}
      </div>

      <p className="mt-1 border-t border-slate-200 px-2 pb-1 pt-2 text-[11px] font-medium uppercase tracking-wide text-slate-500 dark:border-slate-800">
        Width
      </p>
      <div className="flex gap-1 px-1 pb-1">
        {widths.map((width) => <button key={width} type="button" role="menuitemradio"
          aria-checked={width === placement.width}
          aria-label={widthLabel[width]}
          onClick={() => controls.onWidth(width)}
          className={cn('flex-1 rounded-md border px-1 py-1 text-[11px] focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600',
            width === placement.width
              ? 'border-blue-600 bg-blue-50 text-blue-700 dark:bg-blue-500/15 dark:text-blue-300'
              : 'border-slate-200 text-slate-600 hover:bg-slate-100 dark:border-slate-700 dark:text-slate-300 dark:hover:bg-slate-800')}>
          {widthLabel[width]}
        </button>)}
      </div>

      <div className="mt-1 border-t border-slate-200 pt-1 dark:border-slate-800">
        <button type="button" role="menuitem" disabled={controls.index === 0}
          onClick={() => controls.onMove(controls.index - 1)}
          className="flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-sm hover:bg-slate-100 disabled:opacity-40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:hover:bg-slate-800">
          <ChevronRight size={15} aria-hidden className="rotate-180 text-slate-400" />Move earlier
        </button>
        <button type="button" role="menuitem" disabled={controls.index === controls.count - 1}
          onClick={() => controls.onMove(controls.index + 1)}
          className="flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-sm hover:bg-slate-100 disabled:opacity-40 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:hover:bg-slate-800">
          <ChevronRight size={15} aria-hidden className="text-slate-400" />Move later
        </button>
        <button type="button" role="menuitem"
          onClick={() => { controls.onRemove(); setOpen(false) }}
          className="flex w-full items-center gap-2 rounded-lg px-2 py-1.5 text-left text-sm text-red-600 hover:bg-red-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:hover:bg-red-500/10">
          <Trash2 size={15} aria-hidden />Remove from view
        </button>
      </div>
    </div>}
  </div>
}

function Row({ row }: { row: DashboardRow }) {
  const to = dashboardHref(row.link)
  const body = <>
    <span className={cn('mt-1.5 size-2 shrink-0 rounded-full', toneDot[row.tone])} aria-hidden />
    <div className="min-w-0 flex-1">
      <p className="truncate text-sm font-medium text-slate-900 dark:text-slate-100">{row.title}</p>
      <p className="mt-0.5 truncate text-xs text-slate-500">
        {row.subtitle}
        {row.at && <span className="ml-2">{formatLocal(row.at)}</span>}
      </p>
    </div>
    {row.badge && <span className={cn('shrink-0 rounded-md px-2 py-0.5 text-xs font-medium', tonePill[row.tone])}>
      {row.badge}
    </span>}
  </>

  return <li>
    {to
      ? <Link to={to} draggable={false}
          className="group -mx-2 flex items-start gap-2.5 rounded-lg px-2 py-2.5 hover:bg-slate-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:hover:bg-slate-800/60">
          {body}
        </Link>
      : <span className="-mx-2 flex items-start gap-2.5 px-2 py-2.5">{body}</span>}
  </li>
}
