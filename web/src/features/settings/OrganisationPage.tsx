import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ArrowLeft, Building2, MapPin, Pencil, Plus, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Link } from 'react-router-dom'
import { toast } from 'sonner'
import { ApiError } from '../../api/client'
import {
  directoryApi,
  type AdminDepartment,
  type AdminSite,
  type SaveDepartmentInput,
  type SaveSiteInput,
} from '../../api/directory'
import { Button } from '../../components/ui/Button'

type Tab = 'departments' | 'locations'

/**
 * Departments and the locations they operate at. One screen rather than two, because the two lists
 * are the same many-to-many read from opposite ends: a department is edited with its locations, and a
 * location with its departments, and either edit writes the same link rows.
 */
export function OrganisationPage() {
  const queryClient = useQueryClient()
  const [tab, setTab] = useState<Tab>('departments')
  const [editingDepartment, setEditingDepartment] = useState<AdminDepartment | null>(null)
  const [editingSite, setEditingSite] = useState<AdminSite | null>(null)
  const [dialog, setDialog] = useState<Tab | null>(null)

  const departments = useQuery({
    queryKey: ['admin-departments'],
    queryFn: directoryApi.listAdminDepartments,
    meta: { suppressErrorToast: true },
  })
  const sites = useQuery({
    queryKey: ['admin-sites'],
    queryFn: directoryApi.listAdminSites,
    meta: { suppressErrorToast: true },
  })

  /**
   * Both lists move whenever either side is written, because one link row appears in a department's
   * locations and in a location's departments at the same time.
   *
   * The last two keys are the read-only pickers elsewhere in the app, and there are deliberately two
   * of them: most read <c>['directory', …]</c> (the CI lifecycle drawer, bulk edit), but the drift
   * report and audit sessions use a flat <c>['directory-sites']</c>, which no prefix match on the
   * first would ever reach.
   */
  const refresh = async () => {
    await Promise.all([
      queryClient.invalidateQueries({ queryKey: ['admin-departments'] }),
      queryClient.invalidateQueries({ queryKey: ['admin-sites'] }),
      queryClient.invalidateQueries({ queryKey: ['directory'] }),
      queryClient.invalidateQueries({ queryKey: ['directory-sites'] }),
    ])
  }

  const saveDepartment = useMutation({
    mutationFn: (input: SaveDepartmentInput) => editingDepartment
      ? directoryApi.updateDepartment(editingDepartment.id, input)
      : directoryApi.createDepartment(input),
    onSuccess: async (department) => {
      await refresh()
      toast.success(`${department.name} ${editingDepartment ? 'updated' : 'created'}`)
      closeDialog()
    },
  })

  const saveSite = useMutation({
    mutationFn: (input: SaveSiteInput) => editingSite
      ? directoryApi.updateSite(editingSite.id, input)
      : directoryApi.createSite(input),
    onSuccess: async (site) => {
      await refresh()
      toast.success(`${site.name} ${editingSite ? 'updated' : 'created'}`)
      closeDialog()
    },
  })

  const removeDepartment = useMutation({
    mutationFn: (department: AdminDepartment) => directoryApi.deleteDepartment(department.id),
    onSuccess: async () => { await refresh(); toast.success('Department deleted') },
    onError: (error: Error) => toast.error(error.message),
  })

  const removeSite = useMutation({
    mutationFn: (site: AdminSite) => directoryApi.deleteSite(site.id),
    onSuccess: async () => { await refresh(); toast.success('Location deleted') },
    onError: (error: Error) => toast.error(error.message),
  })

  const pending = saveDepartment.isPending || saveSite.isPending

  function closeDialog() {
    if (pending) return
    setDialog(null)
    setEditingDepartment(null)
    setEditingSite(null)
    saveDepartment.reset()
    saveSite.reset()
  }

  const departmentList = departments.data ?? []
  const siteList = sites.data ?? []

  return <div className="space-y-6">
    <div className="flex flex-wrap items-center gap-3">
      <Link to="/admin/settings" className="inline-flex items-center gap-2 text-sm text-slate-500 hover:text-blue-600"><ArrowLeft size={17} />Back to settings</Link>
      <Button className="ml-auto" onClick={() => { setEditingDepartment(null); setEditingSite(null); setDialog(tab) }}>
        <Plus size={18} />{tab === 'departments' ? 'New department' : 'New location'}
      </Button>
    </div>

    <div className="flex gap-1 border-b border-slate-200 dark:border-slate-800" role="tablist">
      {(['departments', 'locations'] as const).map((value) => <button key={value} role="tab" type="button"
        aria-selected={tab === value}
        onClick={() => setTab(value)}
        className={`-mb-px border-b-2 px-4 py-2 text-sm font-medium transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 ${tab === value ? 'border-blue-600 text-blue-600' : 'border-transparent text-slate-500 hover:text-slate-900 dark:hover:text-slate-100'}`}>
        {value === 'departments' ? 'Departments' : 'Locations'}
      </button>)}
    </div>

    {tab === 'departments'
      ? <ListCard
          query={departments}
          emptyIcon={<Building2 />}
          emptyTitle="No departments yet"
          emptyBody="Departments are how tickets and assets are reported on by part of the business."
          errorTitle="Departments could not be loaded"
          headers={['Code', 'Name', 'Locations', 'People', '']}
          onCreate={() => { setEditingDepartment(null); setDialog('departments') }}
          rows={departmentList.map((department) => ({
            key: department.id,
            cells: [
              <span className="font-mono text-xs text-slate-500">{department.code}</span>,
              <span className="font-medium text-slate-900 dark:text-slate-100">{department.name}</span>,
              <Chips names={department.sites.map((site) => site.name)} empty="No locations" />,
              <span className="tabular-nums text-slate-600 dark:text-slate-300">{department.userCount}</span>,
            ],
            onEdit: () => { setEditingDepartment(department); setDialog('departments') },
            onDelete: () => removeDepartment.mutate(department),
            deleteDisabled: removeDepartment.isPending,
            name: department.name,
          }))} />
      : <ListCard
          query={sites}
          emptyIcon={<MapPin />}
          emptyTitle="No locations yet"
          emptyBody="Locations are the sites assets sit at and people work from."
          errorTitle="Locations could not be loaded"
          headers={['Code', 'Name', 'Departments', 'People', '']}
          onCreate={() => { setEditingSite(null); setDialog('locations') }}
          rows={siteList.map((site) => ({
            key: site.id,
            cells: [
              <span className="font-mono text-xs text-slate-500">{site.code}</span>,
              <span className="font-medium text-slate-900 dark:text-slate-100">{site.name}</span>,
              <Chips names={site.departments.map((department) => department.name)} empty="No departments" />,
              <span className="tabular-nums text-slate-600 dark:text-slate-300">{site.userCount}</span>,
            ],
            onEdit: () => { setEditingSite(site); setDialog('locations') },
            onDelete: () => removeSite.mutate(site),
            deleteDisabled: removeSite.isPending,
            name: site.name,
          }))} />}

    {dialog === 'departments' && <OrganisationDialog
      key={editingDepartment?.id ?? 'new-department'}
      title={editingDepartment ? 'Edit department' : 'New department'}
      linkLabel="Locations"
      linkHint="Where this department operates. A department can be at several."
      initial={editingDepartment
        ? { code: editingDepartment.code, name: editingDepartment.name, linkIds: editingDepartment.sites.map((site) => site.id) }
        : { code: '', name: '', linkIds: [] }}
      options={siteList.map((site) => ({ id: site.id, label: site.name }))}
      pending={saveDepartment.isPending}
      error={saveDepartment.error instanceof Error ? saveDepartment.error.message : undefined}
      onClose={closeDialog}
      onSubmit={({ code, name, linkIds }) => saveDepartment.mutate({ code, name, siteIds: linkIds })} />}

    {dialog === 'locations' && <OrganisationDialog
      key={editingSite?.id ?? 'new-location'}
      title={editingSite ? 'Edit location' : 'New location'}
      linkLabel="Departments"
      linkHint="Which departments are present at this location."
      initial={editingSite
        ? { code: editingSite.code, name: editingSite.name, linkIds: editingSite.departments.map((department) => department.id) }
        : { code: '', name: '', linkIds: [] }}
      options={departmentList.map((department) => ({ id: department.id, label: department.name }))}
      pending={saveSite.isPending}
      error={saveSite.error instanceof Error ? saveSite.error.message : undefined}
      onClose={closeDialog}
      onSubmit={({ code, name, linkIds }) => saveSite.mutate({ code, name, departmentIds: linkIds })} />}
  </div>
}

type Row = {
  key: string
  name: string
  cells: React.ReactNode[]
  onEdit: () => void
  onDelete: () => void
  deleteDisabled: boolean
}

function ListCard({ query, headers, rows, emptyIcon, emptyTitle, emptyBody, errorTitle, onCreate }: {
  query: { isLoading: boolean; isError: boolean; error: unknown; refetch: () => unknown }
  headers: string[]
  rows: Row[]
  emptyIcon: React.ReactNode
  emptyTitle: string
  emptyBody: string
  errorTitle: string
  onCreate: () => void
}) {
  return <section className="rounded-xl border border-slate-200 bg-white dark:border-slate-800 dark:bg-slate-900">
    {query.isLoading ? <div aria-label="Loading" className="space-y-px p-4">{Array.from({ length: 4 }, (_, index) => <div key={index} className="h-12 animate-pulse rounded bg-slate-100 dark:bg-slate-800" />)}</div>
      : query.isError ? <div role="alert" className="grid min-h-64 place-items-center p-8 text-center"><div>
          <h2 className="font-semibold">{errorTitle}</h2>
          <p className="mt-1 text-sm text-slate-500">{query.error instanceof ApiError ? query.error.message : 'Try again in a moment.'}</p>
          <Button className="mt-4" variant="secondary" onClick={() => void query.refetch()}>Try again</Button>
        </div></div>
      : rows.length === 0 ? <div className="grid min-h-64 place-items-center p-8 text-center"><div>
          <span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15">{emptyIcon}</span>
          <h2 className="mt-3 font-semibold">{emptyTitle}</h2>
          <p className="mt-1 text-sm text-slate-500">{emptyBody}</p>
          <Button className="mt-4" onClick={onCreate}>Add one</Button>
        </div></div>
      : <div className="overflow-x-auto">
          <table className="w-full min-w-[760px] text-left text-sm">
            <thead><tr>{headers.map((header) => <th key={header} className="h-11 px-4 text-[13px] font-medium text-slate-500">{header}</th>)}</tr></thead>
            <tbody>
              {rows.map((row) => <tr key={row.key} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">
                {row.cells.map((cell, index) => <td key={index} className="h-12 px-4 align-middle">{cell}</td>)}
                <td className="h-12 px-4 text-right whitespace-nowrap">
                  <Button variant="ghost" className="h-8 px-2 text-[13px]" aria-label={`Edit ${row.name}`} onClick={row.onEdit}><Pencil size={15} />Edit</Button>
                  <Button variant="ghost" className="h-8 px-2 text-[13px]" aria-label={`Delete ${row.name}`} disabled={row.deleteDisabled}
                    onClick={() => { if (window.confirm(`Delete ${row.name}? Anything still assigned to it has to be moved first.`)) row.onDelete() }}>
                    <Trash2 size={15} />Delete
                  </Button>
                </td>
              </tr>)}
            </tbody>
          </table>
        </div>}
  </section>
}

function Chips({ names, empty }: { names: string[]; empty: string }) {
  if (names.length === 0) return <span className="text-[13px] text-slate-400">{empty}</span>
  return <span className="flex flex-wrap gap-1">
    {names.map((name) => <span key={name} className="rounded-md bg-slate-100 px-2 py-0.5 text-xs font-medium text-slate-600 dark:bg-slate-800 dark:text-slate-300">{name}</span>)}
  </span>
}

type DialogValue = { code: string; name: string; linkIds: string[] }

function OrganisationDialog({ title, linkLabel, linkHint, initial, options, pending, error, onClose, onSubmit }: {
  title: string
  linkLabel: string
  linkHint: string
  initial: DialogValue
  options: { id: string; label: string }[]
  pending: boolean
  error?: string
  onClose: () => void
  /** The mutation's own error state renders the alert, so this does not await and cannot reject. */
  onSubmit: (value: DialogValue) => void
}) {
  // The caller keys this component by the row being edited, so mounting is the reset and there is no
  // effect syncing props into state.
  const [form, setForm] = useState<DialogValue>(initial)

  const toggle = (id: string) => setForm((current) => ({
    ...current,
    linkIds: current.linkIds.includes(id)
      ? current.linkIds.filter((value) => value !== id)
      : [...current.linkIds, id],
  }))

  return <div className="fixed inset-0 z-30 grid place-items-center bg-slate-900/40 p-4" role="dialog" aria-modal="true" aria-label={title}>
    <form className="w-full max-w-lg rounded-xl border border-slate-200 bg-white p-6 dark:border-slate-800 dark:bg-slate-900"
      onSubmit={(event) => { event.preventDefault(); onSubmit({ ...form, code: form.code.trim(), name: form.name.trim() }) }}>
      <h2 className="text-lg font-semibold">{title}</h2>

      <div className="mt-5 grid gap-4 sm:grid-cols-2">
        <div>
          <label htmlFor="org-code" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Code</label>
          <input id="org-code" required maxLength={50} autoFocus className="input h-11 font-mono"
            value={form.code} onChange={(event) => setForm((current) => ({ ...current, code: event.target.value }))} />
        </div>
        <div>
          <label htmlFor="org-name" className="mb-1.5 block text-[13px] font-medium text-slate-600 dark:text-slate-300">Name</label>
          <input id="org-name" required maxLength={200} className="input h-11"
            value={form.name} onChange={(event) => setForm((current) => ({ ...current, name: event.target.value }))} />
        </div>
      </div>

      <fieldset className="mt-4">
        <legend className="text-[13px] font-medium text-slate-600 dark:text-slate-300">{linkLabel}</legend>
        <p className="mt-1 text-[13px] text-slate-500">{linkHint}</p>
        {options.length === 0
          ? <p className="mt-2 text-[13px] text-slate-400">Nothing to choose from yet.</p>
          : <div className="mt-2 max-h-44 space-y-1 overflow-y-auto rounded-lg border border-slate-200 p-2 dark:border-slate-800">
              {options.map((option) => <label key={option.id} className="flex items-center gap-2 rounded px-2 py-1 text-sm hover:bg-slate-50 dark:hover:bg-slate-800/50">
                <input type="checkbox" className="size-4 rounded border-slate-300 text-blue-600 focus-visible:ring-2 focus-visible:ring-blue-500"
                  checked={form.linkIds.includes(option.id)} onChange={() => toggle(option.id)} />
                {option.label}
              </label>)}
            </div>}
      </fieldset>

      {error && <p role="alert" className="mt-4 text-xs text-red-600">{error}</p>}

      <div className="mt-6 flex justify-end gap-2">
        <Button type="button" variant="secondary" disabled={pending} onClick={onClose}>Cancel</Button>
        <Button type="submit" disabled={pending || !form.code.trim() || !form.name.trim()}>{pending ? 'Saving…' : 'Save'}</Button>
      </div>
    </form>
  </div>
}
