import { Bar, BarChart, Cell, Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import { Link, useNavigate } from 'react-router-dom'
import type { DashboardDisplay, DashboardSegment } from '../../api/dashboard'
import { cn } from '../../lib/utils'
import { dashboardHref, toneDot, toneHex, toneText } from './dashboardUi'

/**
 * A widget's bands drawn as a shape rather than a list (WP-5.5), styled per DESIGN §7: a 65% inner radius
 * on the donut with the total in the middle, dashed gridlines only on the bars, 12px muted labels, no axis
 * lines, and the same semantic colours every pill on these screens uses.
 *
 * It reads segments and nothing else, which is the whole reason a card knows whether it can be charted:
 * a widget that reports only rows has nothing to plot, and asks for a card instead.
 */
export function DashboardWidgetChart({ display, segments, total, totalLabel, title }: {
  display: Exclude<DashboardDisplay, 'Card'>
  segments: DashboardSegment[]
  total: number
  totalLabel: string | null
  title: string
}) {
  const navigate = useNavigate()
  const drawable = segments.filter((segment) => segment.value > 0)

  // Every band at nought. A ring of nothing is either a full circle in one arbitrary colour or an empty
  // outline, and both read as a chart that failed rather than as an estate with nothing in it (WP-2.11).
  if (drawable.length === 0) {
    return <div className="flex flex-1 flex-col">
      <p className="grid flex-1 place-items-center rounded-lg border border-dashed border-slate-200 py-8 text-sm text-slate-500 dark:border-slate-700">
        Nothing to chart yet.
      </p>
      <Legend segments={segments} />
    </div>
  }

  const data = drawable.map((segment) => ({
    name: segment.label,
    value: segment.value,
    fill: toneHex[segment.tone],
    href: dashboardHref(segment.link),
  }))

  const open = (index: number) => {
    const href = data[index]?.href
    if (href) navigate(href)
  }

  return <div className="flex flex-1 flex-col">
    {/* Hidden from assistive technology: a chart is a picture, and the same numbers are in the legend
        underneath where they can actually be read out. */}
    <div aria-hidden className="h-40 w-full">
      <ResponsiveContainer width="100%" height="100%">
        {display === 'Donut'
          ? <PieChart>
              <Pie data={data} dataKey="value" nameKey="name" innerRadius="65%" outerRadius="100%"
                paddingAngle={1} stroke="none" isAnimationActive={false}
                onClick={(_, index) => open(index)}>
                {data.map((entry) => <Cell key={entry.name} fill={entry.fill}
                  className={entry.href ? 'cursor-pointer' : undefined} />)}
              </Pie>
              {/* The centre carries the total, per DESIGN §7 — a donut without one is a shape with no
                  quantity anywhere on it. */}
              <text x="50%" y="50%" textAnchor="middle" dominantBaseline="middle"
                className="fill-slate-900 text-[22px] font-bold dark:fill-slate-100">
                {total}
              </text>
              <Tooltip formatter={(value, name) => [value, String(name)]}
                contentStyle={{ borderRadius: 12, border: '1px solid #E2E8F0', fontSize: 13 }} />
            </PieChart>
          : <BarChart data={data} layout="vertical" margin={{ top: 4, right: 8, bottom: 0, left: 0 }}>
              <XAxis type="number" hide />
              <YAxis type="category" dataKey="name" axisLine={false} tickLine={false} width={92}
                tick={{ fill: '#64748B', fontSize: 12 }} />
              <Tooltip cursor={{ fill: '#F1F5F9' }}
                formatter={(value, name) => [value, String(name)]}
                contentStyle={{ borderRadius: 12, border: '1px solid #E2E8F0', fontSize: 13 }} />
              <Bar dataKey="value" radius={4} isAnimationActive={false} onClick={(_, index) => open(index)}>
                {data.map((entry) => <Cell key={entry.name} fill={entry.fill}
                  className={entry.href ? 'cursor-pointer' : undefined} />)}
              </Bar>
            </BarChart>}
      </ResponsiveContainer>
    </div>

    {totalLabel && <p aria-hidden className="mt-1 text-center text-[13px] text-slate-500">{totalLabel}</p>}
    <Legend segments={segments} title={title} />
  </div>
}

/**
 * The numbers under the picture, and the only copy of them a screen reader is given. Also where the deep
 * links live: a chart segment is a small target and a legend row is not.
 */
function Legend({ segments, title }: { segments: DashboardSegment[]; title?: string }) {
  return <ul aria-label={title ? `${title} values` : undefined}
    className="mt-3 grid gap-0.5 border-t border-slate-200 pt-2 dark:border-slate-800">
    {segments.map((segment) => {
      const href = dashboardHref(segment.link)
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
        {href
          ? <Link to={href} draggable={false}
              className="-mx-2 flex items-center gap-2.5 rounded-lg px-2 py-1 hover:bg-slate-50 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-600 dark:hover:bg-slate-800/60">
              {body}
            </Link>
          : <span className="-mx-2 flex items-center gap-2.5 px-2 py-1">{body}</span>}
      </li>
    })}
  </ul>
}
