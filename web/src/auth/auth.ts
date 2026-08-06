import { UserManager, WebStorageStateStore, type User, type UserManagerSettings } from 'oidc-client-ts'

export type AppRole = 'Admin' | 'Technician' | 'Manager' | 'EndUser'

export type CurrentUser = {
  id: string
  name: string
  email: string | null
  roles: string[]
}

const settings: UserManagerSettings = {
  authority: import.meta.env.VITE_OIDC_AUTHORITY ?? 'http://localhost:8080/realms/it-platform',
  client_id: import.meta.env.VITE_OIDC_CLIENT_ID ?? 'it-platform-web',
  redirect_uri: `${window.location.origin}/auth/callback`,
  post_logout_redirect_uri: window.location.origin,
  response_type: 'code',
  scope: 'openid profile email',
  automaticSilentRenew: true,
  silent_redirect_uri: `${window.location.origin}/auth/silent-callback`,
  userStore: new WebStorageStateStore({ store: window.sessionStorage }),
}

export const userManager = new UserManager(settings)

export function normalizeRoles(claimedRoles: unknown): AppRole[] {
  const allowedRoles: AppRole[] = ['Admin', 'Technician', 'Manager', 'EndUser']
  if (!Array.isArray(claimedRoles)) return []
  return claimedRoles.filter((role): role is AppRole =>
    typeof role === 'string' && allowedRoles.includes(role as AppRole),
  )
}
