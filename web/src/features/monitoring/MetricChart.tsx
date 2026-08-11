import { Area, AreaChart, CartesianGrid, Line, ReferenceLine, ResponsiveContainer, Tooltip, XAxis, YAxis } from 'recharts'
import type { MetricSeries } from '../../api/monitoring'
import { axisFormatter, resolutionLabel, type RangeKey } from './metricRanges'
import { formatLocal } from './severity'

/**
 * One metric over time, styled per DESIGN.md §7: a 2px blue line over a vertical gradient fill,
 * dashed slate gridlines only, no axis lines, muted 12px labels. Threshold lines are dashed in the
 * warning and critical colours, which is the same red and amber every pill on these screens uses.
 */
export function MetricChart({ series, range, warning, critical }: {
  series: MetricSeries
  range: RangeKey
  warning?: number | null
  critical?: number | null
}) {
  const formatAxis = axisFormatter(range)
  const data = series.points.map((point) => ({
    timestamp: point.timestamp,
    value: point.value,
    minValue: point.minValue,
    maxValue: point.maxValue,
  }))

  if (data.length === 0) {
    return <div className="grid h-64 place-items-center rounded-lg border border-dashed border-slate-200 text-sm text-slate-500 dark:border-slate-700">
      No readings in this range.
    </div>
  }

  // Over the rollup each point is an average of five minutes, so the floor and ceiling are drawn as a
  // faint band behind the line: an average alone hides the spike that raised the alert.
  const banded = series.resolution !== 'Raw'

  return <div>
    <div className="h-64 w-full">
      <ResponsiveContainer width="100%" height="100%">
        <AreaChart data={data} margin={{ top: 8, right: 8, bottom: 0, left: 0 }}>
          <defs>
            <linearGradient id="metric-fill" x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor="#2563EB" stopOpacity={0.15} />
              <stop offset="100%" stopColor="#2563EB" stopOpacity={0} />
            </linearGradient>
          </defs>
          <CartesianGrid stroke="#E2E8F0" strokeDasharray="4 4" vertical={false} />
          <XAxis dataKey="timestamp" tickFormatter={formatAxis} axisLine={false} tickLine={false}
            tick={{ fill: '#64748B', fontSize: 12 }} minTickGap={32} />
          <YAxis axisLine={false} tickLine={false} width={56}
            tick={{ fill: '#64748B', fontSize: 12 }}
            label={series.unit ? { value: series.unit, angle: -90, position: 'insideLeft', fill: '#64748B', fontSize: 12 } : undefined} />
          <Tooltip
            labelFormatter={(value) => formatLocal(String(value))}
            formatter={(value, name) => [
              `${value ?? '—'}${series.unit ? ` ${series.unit}` : ''}`,
              name === 'value' ? series.metric : String(name),
            ]}
            contentStyle={{ borderRadius: 12, border: '1px solid #E2E8F0', fontSize: 13 }} />
          {banded && <Area type="monotone" dataKey="maxValue" stroke="none" fill="#2563EB" fillOpacity={0.08} isAnimationActive={false} />}
          {banded && <Area type="monotone" dataKey="minValue" stroke="none" fill="#FFFFFF" fillOpacity={0.9} isAnimationActive={false} />}
          <Area type="monotone" dataKey="value" stroke="#2563EB" strokeWidth={2} fill="url(#metric-fill)" isAnimationActive={false} />
          {typeof warning === 'number' && <ReferenceLine y={warning} stroke="#D97706" strokeDasharray="6 4"
            label={{ value: 'Warning', position: 'right', fill: '#D97706', fontSize: 12 }} />}
          {typeof critical === 'number' && <ReferenceLine y={critical} stroke="#DC2626" strokeDasharray="6 4"
            label={{ value: 'Critical', position: 'right', fill: '#DC2626', fontSize: 12 }} />}
          <Line type="monotone" dataKey="value" stroke="#2563EB" strokeWidth={2} dot={false} isAnimationActive={false} />
        </AreaChart>
      </ResponsiveContainer>
    </div>
    {/* The server says which resolution actually answered — never the Auto that was asked for — so
        the reader can tell a gap in the data from a gap in the chart. */}
    <p className="mt-2 text-[13px] text-slate-500">
      {series.points.length} point{series.points.length === 1 ? '' : 's'} · {resolutionLabel(series.resolution, series.bucketSeconds)}
    </p>
  </div>
}
