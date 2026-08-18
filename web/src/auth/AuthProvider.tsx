import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import type { User } from 'oidc-client-ts'
import { apiRequest } from '../api/client'
import { normalizeRoles, userManager, type AppRole, type CurrentUser } from './auth'

type AuthContextValue = {
  /** The OIDC session. Its subject is Keycloak's own id and matches nothing the modules stored. */
  user: User | null
  /** What /api/me answered, including the sign-in name a ticket records as its assignee. */
  account: CurrentUser | null
  roles: AppRole[]
  isLoading: boolean
  signIn: (returnTo?: string) => Promise<void>
  signOut: () => Promise<void>
}

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null)
  const [roles, setRoles] = useState<AppRole[]>([])
  /**
   * What /api/me answered. Already fetched for the roles; kept because it carries the sign-in name,
   * which is the identity the helpdesk records against a ticket — the OIDC subject matches nothing.
   */
  const [account, setAccount] = useState<CurrentUser | null>(null)
  const [isLoading, setIsLoading] = useState(true)

  useEffect(() => {
    let active = true
    const updateUser = async (nextUser: User | null) => {
      if (!active) return
      setUser(nextUser)
      if (!nextUser || nextUser.expired) {
        setRoles([])
        setAccount(null)
        setIsLoading(false)
        return
      }
      try {
        const currentUser = await apiRequest<CurrentUser>('/api/me')
        if (active) {
          setRoles(normalizeRoles(currentUser.roles))
          setAccount(currentUser)
        }
      } catch {
        if (active) {
          setRoles([])
          setAccount(null)
        }
      } finally {
        if (active) setIsLoading(false)
      }
    }
    const handleUserLoaded = (nextUser: User) => void updateUser(nextUser)
    const clearUser = () => void updateUser(null)
    userManager.events.addUserLoaded(handleUserLoaded)
    userManager.events.addUserUnloaded(clearUser)
    void userManager.getUser().then(updateUser)
    return () => {
      active = false
      userManager.events.removeUserLoaded(handleUserLoaded)
      userManager.events.removeUserUnloaded(clearUser)
    }
  }, [])

  const signIn = useCallback(async (returnTo = '/') => {
    await userManager.signinRedirect({ state: { returnTo } })
  }, [])

  const signOut = useCallback(async () => {
    await userManager.signoutRedirect()
  }, [])

  const value = useMemo(
    () => ({ user, account, roles, isLoading, signIn, signOut }),
    [user, account, roles, isLoading, signIn, signOut])
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth must be used within AuthProvider')
  return context
}
