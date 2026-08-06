import { useEffect, useRef } from 'react'
import { useNavigate } from 'react-router-dom'
import { userManager } from '../auth/auth'

export function AuthCallbackPage({ silent = false }: { silent?: boolean }) {
  const navigate = useNavigate()
  const handled = useRef(false)
  useEffect(() => {
    if (handled.current) return
    handled.current = true
    if (silent) {
      void userManager.signinSilentCallback()
      return
    }
    void userManager.signinRedirectCallback().then((user) => {
      const state = user.state as { returnTo?: string } | undefined
      navigate(state?.returnTo ?? '/', { replace: true })
    }).catch(() => navigate('/login', { replace: true }))
  }, [navigate, silent])
  return <main className="grid min-h-screen place-items-center bg-slate-50 dark:bg-slate-950"><p className="text-sm text-slate-500">Completing sign in…</p></main>
}
