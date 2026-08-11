import type { MetricResolution } from '../../api/monitoring'

export type RangeKey = '1h' | '6h' | '24h' | '7d' | '30d'

export type MetricRange = {
  key: RangeKey
  label: string
  seconds: number
  /**
   * What to ask the server for. `Auto` reads raw up to six hours and the five-minute rollup beyond
   * it; the API refuses `Raw` over more than a day rather than truncating (WP-3.4), so the picker
   * never offers a combination the server would reject.
   */
  resolution: MetricResolution
}

export const metricRanges: MetricRange[] = [
  { key: '1h', label: 'Last hour', seconds: 3_600, resolution: 'Raw' },
  { key: '6h', label: 'Last 6 hours', seconds: 21_600, resolution: 'Auto' },
  { key: '24h', label: 'Last 24 hours', seconds: 86_400, resolution: 'Auto' },
  { key: '7d', label: 'Last 7 days', seconds: 604_800, resolution: 'FiveMinute' },
  { key: '30d', label: 'Last 30 days', seconds: 2_592_000, resolution: 'FiveMinute' },
]

export function rangeFor(key: RangeKey) {
  return metricRanges.find((range) => range.key === key) ?? metricRanges[2]
}

/**
 * The window a range means, anchored to now. Returned as ISO strings because that is what the query
 * API takes and what the query key has to be stable on.
 */
export function windowFor(key: RangeKey, now: Date = new Date()) {
  const range = rangeFor(key)
  const to = new Date(now.getTime())
  const from = new Date(to.getTime() - (range.seconds * 1000))
  return { from: from.toISOString(), to: to.toISOString(), resolution: range.resolution }
}

/**
 * A series identity. The same metric name reported by two checks is two different series (WP-3.4), so
 * the check id is part of what a picker entry *is* — collapsing them would hide one behind the other.
 */
export function seriesKey(metric: string, checkId: string) {
  return `${metric}::${checkId}`
}

export function parseSeriesKey(key: string): { metric: string; checkId: string } | null {
  const index = key.lastIndexOf('::')
  if (index < 0) return null
  return { metric: key.slice(0, index), checkId: key.slice(index + 2) }
}

/**
 * How a chart labels its x axis for the range being shown: a clock for anything inside a day, a date
 * once the window spans several. A 30-day chart labelled "14:05" says nothing.
 */
export function axisFormatter(key: RangeKey) {
  const withinADay = rangeFor(key).seconds <= 86_400
  const format = new Intl.DateTimeFormat(undefined, withinADay
    ? { hour: '2-digit', minute: '2-digit' }
    : { month: 'short', day: 'numeric' })
  return (value: string | number) => format.format(new Date(value))
}

/** What the server actually answered with, said in words under the chart. */
export function resolutionLabel(resolution: MetricResolution, bucketSeconds: number) {
  if (resolution === 'Raw') return 'every reading'
  if (bucketSeconds % 60 === 0) return `${bucketSeconds / 60}-minute averages`
  return `${bucketSeconds}-second averages`
}
