import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import type { User } from 'oidc-client-ts'
import { apiRequest } from '../api/client'
import { normalizeRoles, userManager, type AppRole, type CurrentUser } from './auth'

type AuthContextValue = {
  user: User | null
  roles: AppRole[]
  isLoading: boolean
  signIn: (returnTo?: string) => Promise<void>
  signOut: () => Promise<void>
}

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null)
  const [roles, setRoles] = useState<AppRole[]>([])
  const [isLoading, setIsLoading] = useState(true)

  useEffect(() => {
    let active = true
    const updateUser = async (nextUser: User | null) => {
      if (!active) return
      setUser(nextUser)
      if (!nextUser || nextUser.expired) {
        setRoles([])
        setIsLoading(false)
        return
      }
      try {
        const currentUser = await apiRequest<CurrentUser>('/api/me')
        if (active) setRoles(normalizeRoles(currentUser.roles))
      } catch {
        if (active) setRoles([])
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

  const value = useMemo(() => ({ user, roles, isLoading, signIn, signOut }), [user, roles, isLoading, signIn, signOut])
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth() {
  const context = useContext(AuthContext)
  if (!context) throw new Error('useAuth must be used within AuthProvider')
  return context
}
