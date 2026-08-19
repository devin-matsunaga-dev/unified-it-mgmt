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
    /**
     * Which update is the current one. Two of them race on every redirect sign-in: the initial
     * getUser() and the userLoaded event, and whichever settles last must not overwrite a newer
     * answer with a staler one.
     */
    let latest = 0
    const updateUser = async (nextUser: User | null) => {
      if (!active) return
      const request = ++latest
      setUser(nextUser)
      if (!nextUser || nextUser.expired) {
        setRoles([])
        setAccount(null)
        setIsLoading(false)
        return
      }
      /**
       * A user has arrived but their roles have not: they come from /api/me, a round trip away.
       * This has to go back to loading, because the initial getUser() resolves null on the callback
       * page and clears it — and ProtectedRoute reads an empty role list on a user who is present
       * as "forbidden", not as "not answered yet". On loopback /api/me returned before the callback
       * navigated and nothing showed; over a real network it does not, and a QR scan landed on
       * /forbidden with the roles arriving just too late to matter.
       */
      setIsLoading(true)
      try {
        const currentUser = await apiRequest<CurrentUser>('/api/me')
        if (active && request === latest) {
          setRoles(normalizeRoles(currentUser.roles))
          setAccount(currentUser)
        }
      } catch {
        if (active && request === latest) {
          setRoles([])
          setAccount(null)
        }
      } finally {
        if (active && request === latest) setIsLoading(false)
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
