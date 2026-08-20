import { UserManager, WebStorageStateStore, type User, type UserManagerSettings } from 'oidc-client-ts'

export type AppRole = 'Admin' | 'Technician' | 'Manager' | 'EndUser'

export type CurrentUser = {
  /** The OIDC subject. Keycloak mints its own, so it matches nothing the helpdesk stored. */
  id: string
  name: string
  /** The sign-in name, which is the identity a ticket records as its assignee. */
  username: string
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
  /**
   * localStorage, not sessionStorage, because sessionStorage is per-tab and a scanned QR opens a new
   * one: a technician working through a stockroom was sent round the full redirect sign-in on every
   * label. The trade-off is deliberate — the session now survives closing the tab, so a shared or
   * mislaid handset stays signed in until the token expires or someone signs out. oidc-client-ts
   * already keeps its PKCE state in localStorage by default, so this only aligns the user with it.
   */
  userStore: new WebStorageStateStore({ store: window.localStorage }),
}

export const userManager = new UserManager(settings)

export function normalizeRoles(claimedRoles: unknown): AppRole[] {
  const allowedRoles: AppRole[] = ['Admin', 'Technician', 'Manager', 'EndUser']
  if (!Array.isArray(claimedRoles)) return []
  return claimedRoles.filter((role): role is AppRole =>
    typeof role === 'string' && allowedRoles.includes(role as AppRole),
  )
}
