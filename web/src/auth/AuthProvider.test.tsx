import { render, screen, waitFor } from '@testing-library/react'
import type { User } from 'oidc-client-ts'
import { vi } from 'vitest'
import { apiRequest } from '../api/client'
import { AuthProvider, useAuth } from './AuthProvider'
import { userManager } from './auth'

vi.mock('../api/client', () => ({ apiRequest: vi.fn() }))

function RoleList() {
  const { roles, isLoading } = useAuth()
  return <span>{isLoading ? 'Loading' : roles.join(',')}</span>
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
})
