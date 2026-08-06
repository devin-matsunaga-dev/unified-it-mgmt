import { Link } from 'react-router-dom'

export function ForbiddenPage() {
  return <main className="grid min-h-screen place-items-center bg-slate-50 p-6 text-center dark:bg-slate-950"><div><p className="text-sm font-medium text-blue-600">403</p><h1 className="mt-2 text-2xl font-bold">You do not have access</h1><p className="mt-2 text-sm text-slate-500">Your role does not permit access to this page.</p><Link to="/" className="mt-5 inline-block text-sm font-medium text-blue-600 hover:text-blue-700">Return to overview</Link></div></main>
}
