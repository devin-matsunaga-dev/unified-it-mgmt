import { useQuery } from '@tanstack/react-query'
import { AppWindow } from 'lucide-react'
import { Link } from 'react-router-dom'
import { softwareApi } from '../../api/software'

/**
 * What this machine reports having installed. The raw name is what it said; the product beside it is
 * what the catalogue made of that, and a name no rule claims says so rather than guessing.
 */
export function CiSoftwareCard({ ciId }: { ciId: string }) {
  const installs = useQuery({
    queryKey: ['installed-software', { ciId }],
    queryFn: () => softwareApi.listInstalls({ ciId, pageSize: 200 }),
  })

  return <section className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900">
    <h2 className="flex items-center gap-2 font-semibold">
      <AppWindow size={18} className="text-slate-400" />Installed software
      {installs.data && <span className="text-[13px] font-normal text-slate-500">({installs.data.total})</span>}
    </h2>

    {installs.isPending && <div aria-label="Loading installed software" className="mt-4 space-y-2">
      {[0, 1, 2].map((index) => <div key={index} className="h-5 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}
    </div>}

    {installs.isError && <p role="alert" className="mt-3 text-sm text-red-600">
      Installed software could not be loaded.
    </p>}

    {installs.data && (installs.data.items.length === 0
      ? <p className="mt-3 text-sm text-slate-500">
          No inventory has been imported for this asset. It appears here once a software inventory file covering it is imported.
        </p>
      : <>
          <ul className="mt-4 divide-y divide-slate-200 text-sm dark:divide-slate-800">
            {installs.data.items.map((install) => <li key={install.id} className="flex flex-wrap items-baseline gap-x-2 py-2">
              <span className="font-medium text-slate-900 dark:text-slate-100">{install.rawName}</span>
              {install.version && <span className="tabular-nums text-slate-500">{install.version}</span>}
              <span className="ml-auto text-xs">
                {install.productId
                  ? <Link to={`/software/products/${install.productId}`} className="text-slate-500 hover:text-blue-600">{install.productName}</Link>
                  : <span className="text-amber-700 dark:text-amber-400">Unrecognised</span>}
              </span>
            </li>)}
          </ul>
          <p className="mt-4 text-xs text-slate-500">
            Reported by an inventory import. Nothing here changes what the CMDB records about the asset itself.
          </p>
        </>)}
  </section>
}
