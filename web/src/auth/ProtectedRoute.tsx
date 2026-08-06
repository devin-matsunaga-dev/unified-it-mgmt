import type { ReactNode } from 'react'
import { Navigate, useLocation } from 'react-router-dom'
import { type AppRole } from './auth'
import { useAuth } from './AuthProvider'

export function ProtectedRoute({ children, roles = [] }: { children: ReactNode; roles?: AppRole[] }) {
  const { user, roles: currentRoles, isLoading } = useAuth()
  const location = useLocation()

  if (isLoading) return <PageSkeleton />
  if (!user || user.expired) return <Navigate to="/login" state={{ from: location.pathname }} replace />
  if (roles.length > 0 && !roles.some((role) => currentRoles.includes(role))) return <Navigate to="/forbidden" replace />
  return children
}

function PageSkeleton() {
  return <div className="min-h-screen bg-slate-50 p-6 dark:bg-slate-950" aria-label="Loading"><div className="h-12 animate-pulse rounded-xl bg-slate-100 dark:bg-slate-800" /></div>
}
