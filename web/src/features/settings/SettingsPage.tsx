import { useQuery } from '@tanstack/react-query'
import { AlarmClock, Building2, ChevronRight, ListPlus, Tags } from 'lucide-react'
import type { ComponentType } from 'react'
import { Link } from 'react-router-dom'
import { assetsApi } from '../../api/assets'
import { directoryApi } from '../../api/directory'
import { slaApi } from '../../api/sla'
import { helpdeskApi } from '../../api/helpdesk'
import { totalCount } from './categoryTree'

/**
 * The administration area's index. Each section is a card that links to its own page, so a new
 * setting is a new entry here rather than another tab on a page that keeps growing.
 */
export function SettingsPage() {
  const categories = useQuery({
    queryKey: ['ticket-categories', 'all'],
    queryFn: helpdeskApi.listCategoriesIncludingInactive,
    meta: { suppressErrorToast: true },
  })

  const departments = useQuery({
    queryKey: ['admin-departments'],
    queryFn: directoryApi.listAdminDepartments,
    meta: { suppressErrorToast: true },
  })

  const schemas = useQuery({
    queryKey: ['ci-type-schemas'],
    queryFn: assetsApi.listTypeSchemas,
    meta: { suppressErrorToast: true },
  })

  const policies = useQuery({
    queryKey: ['sla-policies'],
    queryFn: slaApi.listPolicies,
    meta: { suppressErrorToast: true },
  })

  const count = categories.data ? totalCount(categories.data) : null
  const policyCount = policies.data?.length ?? null
  const fieldCount = schemas.data
    ? schemas.data.reduce((total, schema) => total + schema.customFields.length, 0)
    : null
  const orgCount = departments.data?.length ?? null

  return <div className="space-y-6">
    <div className="grid gap-6 sm:grid-cols-2">
      <SettingsCard
        to="/admin/settings/ticket-categories"
        icon={Tags}
        title="Ticket categories"
        description="The categories people choose when raising a ticket, and how they nest."
        detail={count === null ? 'Loading…' : `${count} ${count === 1 ? 'category' : 'categories'}`} />

      <SettingsCard
        to="/admin/settings/organisation"
        icon={Building2}
        title="Departments and locations"
        description="The parts of the business, the sites they operate at, and which belongs where."
        detail={orgCount === null ? 'Loading…' : `${orgCount} ${orgCount === 1 ? 'department' : 'departments'}`} />

      <SettingsCard
        to="/admin/settings/asset-fields"
        icon={ListPlus}
        title="Asset fields"
        description="Extra fields each kind of CI carries — and how hardware is split into laptops, desktops and printers."
        detail={fieldCount === null ? 'Loading…' : `${fieldCount} ${fieldCount === 1 ? 'field' : 'fields'}`} />

      <SettingsCard
        to="/admin/settings/sla"
        icon={AlarmClock}
        title="Service levels"
        description="The ordered rules that decide how long a ticket has, and the business hours they run on."
        detail={policyCount === null ? 'Loading…' : `${policyCount} ${policyCount === 1 ? 'policy' : 'policies'}`} />
    </div>
  </div>
}

function SettingsCard({ to, icon: Icon, title, description, detail }: {
  to: string
  icon: ComponentType<{ size?: number }>
  title: string
  description: string
  detail: string
}) {
  return <Link to={to}
    className="group flex gap-4 rounded-xl border border-slate-200 bg-white p-6 transition-colors hover:border-blue-300 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 dark:border-slate-800 dark:bg-slate-900 dark:hover:border-blue-800">
    <span className="grid size-10 shrink-0 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-500/15"><Icon size={20} /></span>
    <span className="min-w-0 flex-1">
      <span className="flex items-center gap-1 font-semibold text-slate-900 dark:text-slate-100">{title}<ChevronRight size={16} className="text-slate-400 transition-transform group-hover:translate-x-0.5" /></span>
      <span className="mt-1 block text-sm text-slate-500">{description}</span>
      <span className="mt-3 block text-[13px] font-medium text-slate-500 tabular-nums">{detail}</span>
    </span>
  </Link>
}
