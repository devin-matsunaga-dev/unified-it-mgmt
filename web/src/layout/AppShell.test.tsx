import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { AppShell } from './AppShell'

/**
 * The shell needs a query client since WP-5.4 put the global search bar in its topbar. Nothing here types
 * into it, so the client never fetches — GlobalSearch.test.tsx is where the bar itself is exercised.
 */
function renderShell() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><AppShell /></QueryClientProvider></MemoryRouter>)
}

const { signOut } = vi.hoisted(() => ({ signOut: vi.fn() }))
vi.mock('../auth/AuthProvider', () => ({ useAuth: () => ({ user: { profile: { name: 'End User', email: 'enduser@example.test' } }, roles: ['EndUser'], signOut }) }))

describe('AppShell', () => {
  it('hides administrative navigation from an EndUser', () => {
    renderShell()
    expect(screen.getByRole('link', { name: 'Tickets' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Users' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Settings' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Monitoring' })).not.toBeInTheDocument()
  })

  it('opens an account menu before signing out', async () => {
    const user = userEvent.setup()
    renderShell()

    await user.click(screen.getByRole('button', { name: /End User/i }))

    expect(signOut).not.toHaveBeenCalled()
    expect(screen.getByRole('menu', { name: 'Account menu' })).toBeInTheDocument()
    expect(screen.getByText('enduser@example.test')).toBeInTheDocument()

    await user.click(screen.getByRole('menuitem', { name: 'Sign out' }))
    expect(signOut).toHaveBeenCalledOnce()
  })
})
