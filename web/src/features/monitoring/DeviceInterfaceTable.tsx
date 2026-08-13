import { EthernetPort } from 'lucide-react'
import type { DeviceInterface } from '../../api/monitoring'
import { cn } from '../../lib/utils'
import { formatAge, formatLocal } from './severity'
import {
  formatBitsPerSecond,
  formatEventRate,
  formatSpeed,
  formatUtilisation,
  interfaceStatusLabel,
  interfaceTone,
  utilisationBarWidth,
} from './interfaceFormat'

/**
 * One switch's ports, as the last poll found them.
 *
 * A table rather than a chart because the first question about an estate's interfaces is "which one
 * is down" and the second is "which one is full" — both of which are answered by scanning a column.
 * The charts are one click further in: selecting a row points the metric chart above at that port,
 * which is what makes a per-interface graph an ordinary series query rather than a second chart
 * component (DESIGN.md §5 density: tight inside data, generous around it).
 */
export function DeviceInterfaceTable({
  interfaces,
  isLoading,
  selectedIfIndex,
  onSelect,
}: {
  interfaces: DeviceInterface[]
  isLoading: boolean
  selectedIfIndex: number | null
  onSelect: (link: DeviceInterface) => void
}) {
  if (isLoading) {
    return <div aria-label="Loading interfaces" className="h-40 animate-pulse rounded-lg bg-slate-100 dark:bg-slate-800" />
  }

  if (interfaces.length === 0) {
    return <div className="flex flex-col items-center gap-3 py-10 text-center">
      <span className="flex size-12 items-center justify-center rounded-full bg-slate-100 dark:bg-slate-800">
        <EthernetPort size={22} className="text-slate-500" aria-hidden />
      </span>
      <p className="text-sm text-slate-500">
        This device has not reported any interfaces. Add an SNMP check with the <code>interfaces</code> metric to poll them.
      </p>
    </div>
  }

  return <div className="overflow-x-auto">
    <table className="w-full min-w-[900px] text-sm">
      <caption className="sr-only">Interfaces on this device</caption>
      <thead>
        <tr className="text-left text-[13px] font-medium text-slate-500">
          <th scope="col" className="py-2 pr-3">Interface</th>
          <th scope="col" className="py-2 pr-3">Status</th>
          <th scope="col" className="py-2 pr-3 text-right">Speed</th>
          <th scope="col" className="py-2 pr-3 text-right">In</th>
          <th scope="col" className="py-2 pr-3 text-right">Out</th>
          <th scope="col" className="py-2 pr-3">Utilisation</th>
          <th scope="col" className="py-2 pr-3 text-right">Errors</th>
          <th scope="col" className="py-2 text-right">Discards</th>
        </tr>
      </thead>
      <tbody>
        {interfaces.map((link) => <tr
          key={link.ifIndex}
          onClick={() => onSelect(link)}
          aria-selected={link.ifIndex === selectedIfIndex}
          className={cn(
            'cursor-pointer border-t border-slate-100 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50',
            link.ifIndex === selectedIfIndex && 'bg-blue-50/60 dark:bg-blue-500/10')}
        >
          <td className="py-2.5 pr-3">
            <button
              type="button"
              onClick={(event) => { event.stopPropagation(); onSelect(link) }}
              className="text-left font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100 dark:hover:text-blue-400"
            >
              {link.name}
            </button>
            {link.alias && <p className="text-[13px] text-slate-500">{link.alias}</p>}
          </td>
          <td className="py-2.5 pr-3">
            <span className={cn('inline-flex rounded-md px-2 py-0.5 text-xs font-medium', interfaceTone(link))}>
              {interfaceStatusLabel(link)}
            </span>
          </td>
          <td className="py-2.5 pr-3 text-right tabular-nums text-slate-600 dark:text-slate-400">
            {formatSpeed(link.speedBitsPerSecond)}
          </td>
          <td className="py-2.5 pr-3 text-right tabular-nums">{formatBitsPerSecond(link.bitsInPerSecond)}</td>
          <td className="py-2.5 pr-3 text-right tabular-nums">{formatBitsPerSecond(link.bitsOutPerSecond)}</td>
          <td className="py-2.5 pr-3">
            <div className="flex items-center gap-2">
              <span className="h-1.5 w-20 shrink-0 rounded-full bg-slate-100 dark:bg-slate-800">
                <span
                  className="block h-1.5 rounded-full bg-blue-600"
                  style={{ width: utilisationBarWidth(link.utilisationPercent) }}
                />
              </span>
              <span className="tabular-nums text-slate-600 dark:text-slate-400">
                {formatUtilisation(link.utilisationPercent)}
              </span>
            </div>
          </td>
          <td className="py-2.5 pr-3 text-right tabular-nums">
            {formatEventRate(sum(link.errorsInPerSecond, link.errorsOutPerSecond))}
          </td>
          <td className="py-2.5 text-right tabular-nums">
            {formatEventRate(sum(link.discardsInPerSecond, link.discardsOutPerSecond))}
          </td>
        </tr>)}
      </tbody>
    </table>
    {interfaces[0] && <p className="mt-3 text-[13px] text-slate-500">
      Last polled <span title={formatLocal(interfaces[0].observedAt)}>{formatAge(interfaces[0].observedAt)}</span>. Select a row to chart it.
    </p>}
  </div>
}

/**
 * Errors in plus errors out, which is what the column claims to be — and null when neither direction
 * was measured, so an unmeasured port is not reported as a clean one. A single measured direction
 * still counts: half an answer about errors is worth more than none.
 */
function sum(first: number | null, second: number | null): number | null {
  if (first === null && second === null) return null
  return (first ?? 0) + (second ?? 0)
}
