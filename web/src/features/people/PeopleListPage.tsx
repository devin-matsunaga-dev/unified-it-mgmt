import { useQuery } from '@tanstack/react-query'
import { Users } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { directoryApi } from '../../api/directory'
import { Button } from '../../components/ui/Button'

/** The way into a person's 360° page. The directory is small, so it filters in the browser. */
export function PeopleListPage() {
  const [search, setSearch] = useState('')
  const users = useQuery({ queryKey: ['directory', 'users'], queryFn: directoryApi.listUsers })
  const term = search.trim().toLowerCase()
  const items = (users.data ?? []).filter((user) => !term
    || [user.displayName, user.username, user.email, user.departmentName, user.siteName]
      .some((field) => field.toLowerCase().includes(term)))

  return <div className="space-y-6">
    <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
      <div className="border-b border-slate-200 p-4 dark:border-slate-800">
        <label className="flex h-10 max-w-md items-center gap-2 rounded-lg border border-slate-200 px-3 text-slate-500 dark:border-slate-700">
          <Users size={17} /><span className="sr-only">Search people</span>
          <input value={search} onChange={(event) => setSearch(event.target.value)} className="w-full bg-transparent text-sm text-slate-900 outline-none dark:text-slate-100" placeholder="Search names, departments, and sites…" />
        </label>
      </div>

      {users.isLoading ? <div aria-label="Loading people" className="space-y-px p-4">{Array.from({ length: 6 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div>
        : users.isError ? <div role="alert" className="grid min-h-48 place-items-center p-8 text-center"><div>
            <h2 className="font-semibold">People could not be loaded</h2>
            <Button className="mt-4" variant="secondary" onClick={() => void users.refetch()}>Try again</Button>
          </div></div>
        : items.length === 0 ? <p className="p-8 text-center text-sm text-slate-500">Nobody matches that search.</p>
        : <div className="overflow-x-auto"><table className="w-full min-w-[720px] text-left text-sm">
            <thead><tr>{['Name', 'Username', 'Role', 'Department', 'Location'].map((header) => <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}</tr></thead>
            <tbody>
              {items.map((user) => <tr key={user.id} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                <td className="h-12 px-4"><Link to={`/people/${user.id}`} className="font-medium text-slate-900 hover:text-blue-600 dark:text-slate-100">{user.displayName}</Link></td>
                <td className="h-12 px-4 font-mono text-xs text-slate-500">{user.username}</td>
                <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{user.role}</td>
                <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{user.departmentName}</td>
                <td className="h-12 px-4 text-slate-600 dark:text-slate-300">{user.siteName}</td>
              </tr>)}
            </tbody>
          </table></div>}
    </section>
  </div>
}
