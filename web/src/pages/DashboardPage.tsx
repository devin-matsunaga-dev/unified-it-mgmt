import { Activity, Boxes, CircleCheck, Headphones, MonitorCog, TrendingDown, TrendingUp, Users } from 'lucide-react'

const stats = [
  { label: 'Total devices', value: '1,428', delta: '4.2%', sentiment: 'up', icon: MonitorCog, tint: 'bg-blue-50 text-blue-600 dark:bg-blue-950' },
  { label: 'Active users', value: '892', delta: '2.1%', sentiment: 'up', icon: Users, tint: 'bg-violet-50 text-violet-600 dark:bg-violet-950' },
  { label: 'Open tickets', value: '43', delta: '8.3%', sentiment: 'down', icon: Headphones, tint: 'bg-amber-50 text-amber-600 dark:bg-amber-950' },
  { label: 'Critical alerts', value: '7', delta: '12.5%', sentiment: 'down', icon: Activity, tint: 'bg-red-50 text-red-600 dark:bg-red-950' },
]

export function DashboardPage() {
  return <div className="space-y-6">
    <section className="grid gap-4 sm:grid-cols-2 xl:grid-cols-4" aria-label="Environment summary">
      {stats.map(({ label, value, delta, sentiment, icon: Icon, tint }) => <article key={label} className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"><div className="flex items-center gap-4"><span className={`grid size-10 place-items-center rounded-full ${tint}`}><Icon size={20} /></span><div><p className="text-[13px] font-medium text-slate-500">{label}</p><p className="mt-1 text-3xl font-bold tabular-nums">{value}</p></div></div><p className="mt-3 flex items-center gap-1 text-xs text-green-600">{sentiment === 'up' ? <TrendingUp size={14} /> : <TrendingDown size={14} />} {delta} <span className="text-slate-500">from last week</span></p></article>)}
    </section>
    <section className="grid gap-6 xl:grid-cols-[2fr_1fr]">
      <article className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"><div className="mb-5 flex items-center justify-between"><h2 className="font-semibold">Recent tickets</h2><span className="text-[13px] text-blue-600">View all</span></div><div className="overflow-x-auto"><table className="w-full min-w-[580px] text-left text-sm"><thead className="text-[13px] font-medium text-slate-500"><tr><th className="pb-3">ID</th><th className="pb-3">Title</th><th className="pb-3">Status</th><th className="pb-3">Priority</th><th className="pb-3">Updated</th></tr></thead><tbody>{[['#INC-1043','Unable to connect to VPN','In progress','High','10:24 AM'],['#INC-1042','Email not syncing on mobile','Open','Medium','9:15 AM'],['#INC-1041','Need access to shared drive','Open','Low','8:45 AM']].map((row) => <tr key={row[0]} className="border-t border-slate-200 hover:bg-slate-50 dark:border-slate-800 dark:hover:bg-slate-800/50">{row.map((cell, i) => <td key={cell} className="h-12 pr-4"><span className={i === 0 ? 'text-slate-500' : i === 2 ? 'rounded-md bg-blue-100 px-2 py-1 text-xs text-blue-700 dark:bg-blue-950 dark:text-blue-300' : i === 3 ? 'rounded-md bg-slate-100 px-2 py-1 text-xs text-slate-600 dark:bg-slate-800 dark:text-slate-300' : ''}>{cell}</span></td>)}</tr>)}</tbody></table></div></article>
      <article className="rounded-xl border border-slate-200 bg-white p-5 dark:border-slate-800 dark:bg-slate-900"><h2 className="mb-4 font-semibold">System health</h2>{['API services','Message bus','Database','Object storage'].map((service) => <div key={service} className="flex h-12 items-center gap-3 border-t border-slate-200 first:border-t-0 dark:border-slate-800"><Boxes size={18} className="text-slate-500" /><span className="flex-1 text-sm">{service}</span><CircleCheck size={16} className="text-green-600" /><span className="text-[13px] text-green-600">Healthy</span></div>)}</article>
    </section>
  </div>
}
