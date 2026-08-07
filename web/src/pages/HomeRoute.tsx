import { Navigate } from 'react-router-dom'
import { useAuth } from '../auth/AuthProvider'
import { DashboardPage } from './DashboardPage'

const agentRoles = ['Admin', 'Technician', 'Manager'] as const

/** End users have no agent surface, so the root route sends them to the self-service portal instead. */
export function HomeRoute() {
  const { roles } = useAuth()
  if (roles.includes('EndUser') && !agentRoles.some((role) => roles.includes(role))) {
    return <Navigate to="/portal" replace />
  }
  return <DashboardPage />
}
