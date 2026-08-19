import { act, render, screen, waitFor } from '@testing-library/react'
import type { User } from 'oidc-client-ts'
import { vi } from 'vitest'
import { apiRequest } from '../api/client'
import { AuthProvider, useAuth } from './AuthProvider'
import type { CurrentUser } from './auth'
import { userManager } from './auth'

vi.mock('../api/client', () => ({ apiRequest: vi.fn() }))

/**
 * The userLoaded handler AuthProvider registered. Captured from addUserLoaded rather than raised
 * through oidc-client-ts, whose event-raising methods are internal to the library.
 */
function captureUserLoaded() {
  const handlers: ((user: User) => void)[] = []
  vi.spyOn(userManager.events, 'addUserLoaded').mockImplementation((handler) => {
    handlers.push(handler as (user: User) => void)
    return () => {}
  })
  return (user: User) => handlers.forEach((handler) => handler(user))
}

function RoleList() {
  const { roles, isLoading } = useAuth()
  return <span data-testid="roles">{isLoading ? 'Loading' : roles.join(',')}</span>
}

describe('AuthProvider', () => {
  it('uses normalized API roles instead of provider-specific profile claims', async () => {
    vi.spyOn(userManager, 'getUser').mockResolvedValue({ expired: false, profile: {} } as User)
    vi.mocked(apiRequest).mockResolvedValue({
      id: 'admin-id',
      name: 'admin1',
      email: 'admin1@example.test',
      roles: ['Admin'],
    })

    render(<AuthProvider><RoleList /></AuthProvider>)

    await waitFor(() => expect(screen.getByText('Admin')).toBeInTheDocument())
    expect(apiRequest).toHaveBeenCalledWith('/api/me')
  })

  /**
   * The QR-scan bug. On the callback page the initial getUser() resolves null and clears isLoading,
   * then userLoaded arrives with the real user and /api/me is still a round trip away. If that gap
   * is not reported as loading, ProtectedRoute sees a present user with no roles and sends a deep
   * link to /forbidden — which is what a scanned label did over a real network and never did on
   * loopback, where /api/me won the race.
   */
  it('reports loading between a user arriving and its roles landing', async () => {
    const raiseUserLoaded = captureUserLoaded()
    vi.spyOn(userManager, 'getUser').mockResolvedValue(null)
    let resolveMe: (value: CurrentUser) => void = () => {}
    vi.mocked(apiRequest).mockReturnValue(new Promise<never>((resolve) => {
      resolveMe = resolve as unknown as (value: CurrentUser) => void
    }))

    render(<AuthProvider><RoleList /></AuthProvider>)

    // The null user settles first, exactly as it does on the callback page.
    await waitFor(() => expect(screen.getByTestId('roles')).toHaveTextContent(''))

    act(() => {
      raiseUserLoaded({ expired: false, profile: {} } as User)
    })

    // The window a deep link renders in: a user is present, /api/me has not answered.
    await waitFor(() => expect(screen.getByText('Loading')).toBeInTheDocument())

    await act(async () => {
      resolveMe({ id: 'a', name: 'admin1', username: 'admin1', email: null, roles: ['Admin'] })
    })
    await waitFor(() => expect(screen.getByText('Admin')).toBeInTheDocument())
  })

  it('ignores a stale /api/me answer that settles after a newer one', async () => {
    const raiseUserLoaded = captureUserLoaded()
    vi.spyOn(userManager, 'getUser').mockResolvedValue({ expired: false, profile: {} } as User)
    let resolveFirst: (value: CurrentUser) => void = () => {}
    vi.mocked(apiRequest)
      .mockReturnValueOnce(new Promise((resolve) => {
        resolveFirst = resolve as unknown as (value: CurrentUser) => void
      }))
      .mockResolvedValue({ id: 'b', name: 'tech1', username: 'tech1', email: null, roles: ['Technician'] })

    render(<AuthProvider><RoleList /></AuthProvider>)

    act(() => {
      raiseUserLoaded({ expired: false, profile: {} } as User)
    })
    await waitFor(() => expect(screen.getByText('Technician')).toBeInTheDocument())

    // The first request answers last. It is stale and must not overwrite the newer roles.
    await act(async () => {
      resolveFirst({ id: 'a', name: 'admin1', username: 'admin1', email: null, roles: ['Admin'] })
    })
    expect(screen.getByText('Technician')).toBeInTheDocument()
  })
})
