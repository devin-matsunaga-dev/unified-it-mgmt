import { Construction } from 'lucide-react'

export function PlaceholderPage({ title }: { title: string }) {
  return <div className="grid min-h-[55vh] place-items-center rounded-xl border border-slate-200 bg-white p-8 text-center dark:border-slate-800 dark:bg-slate-900"><div><span className="mx-auto grid size-12 place-items-center rounded-full bg-blue-50 text-blue-600 dark:bg-blue-950"><Construction /></span><h2 className="mt-4 text-lg font-semibold">{title}</h2><p className="mt-1 text-sm text-slate-500">This area will be enabled in its dedicated work package.</p></div></div>
}
