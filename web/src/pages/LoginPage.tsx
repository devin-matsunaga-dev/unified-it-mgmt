import { ShieldCheck } from 'lucide-react'
import { useLocation } from 'react-router-dom'
import { useAuth } from '../auth/AuthProvider'
import { Button } from '../components/ui/Button'

export function LoginPage() {
  const { signIn } = useAuth()
  const location = useLocation()
  const returnTo = (location.state as { from?: string } | null)?.from ?? '/'
  return <main className="grid min-h-screen place-items-center bg-slate-50 p-6 dark:bg-slate-950"><section className="w-full max-w-md rounded-xl border border-slate-200 bg-white p-8 text-center dark:border-slate-800 dark:bg-slate-900"><span className="mx-auto grid size-12 place-items-center rounded-xl bg-blue-600 text-white"><ShieldCheck size={28} /></span><h1 className="mt-5 text-2xl font-bold">Welcome to ITManager</h1><p className="mt-2 text-sm text-slate-500">Sign in with your organization account to continue.</p><Button className="mt-7 w-full" onClick={() => void signIn(returnTo)}>Sign in</Button></section></main>
}
