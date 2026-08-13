import { useQuery } from '@tanstack/react-query'
import { Radar } from 'lucide-react'
import { ApiError } from '../../api/client'
import { discoveryApi } from '../../api/assets'
import { formatLocal } from '../tickets/ticketUi'

/**
 * What the network last said about this asset, beside what the CMDB records for it.
 *
 * Deliberately a separate card and separate storage rather than fields written into the CI. A scan
 * observes; an operator asserts. Overwriting the recorded attributes with scanned ones would destroy
 * the difference between them, which is exactly the signal WP-4.6's drift report is built to find.
 */
export function CiDiscoveryCard({ ciId }: { ciId: string }) {
  const facts = useQuery({
    queryKey: ['ci-discovery-facts', ciId],
    queryFn: () => discoveryApi.getCiDiscoveryFacts(ciId),
    // A CI no scan has ever reached is the ordinary case, not a failure, so a 404 is not retried.
    retry: (count, error) => !(error instanceof ApiError && error.status === 404) && count < 2,
  })

  const neverSeen = facts.error instanceof ApiError && facts.error.status === 404

  return <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
    <h2 className="flex items-center gap-2 font-semibold"><Radar size={18} className="text-slate-400" />Discovery</h2>

    {facts.isPending && <div aria-label="Loading discovery facts" className="mt-4 space-y-2">
      {[0, 1, 2].map((index) => <div key={index} className="h-5 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}
    </div>}

    {neverSeen && <p className="mt-3 text-sm text-slate-500">
      No scan has reported this asset. It appears here once a scan profile covers the network it is on.
    </p>}

    {facts.isError && !neverSeen && <p role="alert" className="mt-3 text-sm text-red-600">
      Discovery facts could not be loaded.
    </p>}

    {facts.data && <>
      <dl className="mt-4 space-y-3 text-sm">
        <Detail label="Last seen" value={formatLocal(facts.data.lastSeenAt)} />
        <Detail label="Address" value={facts.data.address} />
        {facts.data.hostname && <Detail label="Hostname" value={facts.data.hostname} />}
        {facts.data.snmp?.sysName && <Detail label="Reports its name as" value={facts.data.snmp.sysName} />}
        {/* The closest thing to "firmware" a scan can honestly give: the vendor's own one-line
            description, kept verbatim because no format is shared across vendors. */}
        {facts.data.snmp?.sysDescription && <Detail label="Reports itself as" value={facts.data.snmp.sysDescription} />}
        {facts.data.snmp?.sysLocation && <Detail label="Reports its location as" value={facts.data.snmp.sysLocation} />}
        <Detail label="Open ports" value={facts.data.openPorts.length > 0
          ? facts.data.openPorts.join(', ')
          : facts.data.respondedToPing ? 'None answered (ICMP only)' : 'None answered'} />
        <Detail label="Seen by" value={`${facts.data.scanProfileName} · ${facts.data.sightingCount} scan${facts.data.sightingCount === 1 ? '' : 's'}`} />
      </dl>

      {facts.data.neighbours.length > 0 && <>
        <h3 className="mt-5 text-[13px] font-medium text-slate-500">Neighbours</h3>
        <ul className="mt-2 space-y-1 text-sm text-slate-600 dark:text-slate-300">
          {facts.data.neighbours.map((neighbour, index) => <li key={index}>
            {neighbour.localPort ?? '?'} → {neighbour.remoteSystemName ?? 'unnamed'}
            <span className="ml-1 text-xs uppercase text-slate-400">{neighbour.protocol}</span>
          </li>)}
        </ul>
      </>}

      <p className="mt-4 text-xs text-slate-500">
        Observed on the network. The details above the fold are what the CMDB records — discovery never
        overwrites them.
      </p>
    </>}
  </section>
}

function Detail({ label, value }: { label: string; value: string }) {
  return <div className="flex gap-4">
    <dt className="text-slate-500">{label}</dt>
    <dd className="ml-auto max-w-[65%] break-words text-right font-medium">{value}</dd>
  </div>
}
