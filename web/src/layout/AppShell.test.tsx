import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { AppShell } from './AppShell'

const { signOut } = vi.hoisted(() => ({ signOut: vi.fn() }))
vi.mock('../auth/AuthProvider', () => ({ useAuth: () => ({ user: { profile: { name: 'End User', email: 'enduser@example.test' } }, roles: ['EndUser'], signOut }) }))

describe('AppShell', () => {
  it('hides administrative navigation from an EndUser', () => {
    render(<MemoryRouter><AppShell /></MemoryRouter>)
    expect(screen.getByRole('link', { name: 'Tickets' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Users' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Settings' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Monitoring' })).not.toBeInTheDocument()
  })

  it('opens an account menu before signing out', async () => {
    const user = userEvent.setup()
    render(<MemoryRouter><AppShell /></MemoryRouter>)

    await user.click(screen.getByRole('button', { name: /End User/i }))

    expect(signOut).not.toHaveBeenCalled()
    expect(screen.getByRole('menu', { name: 'Account menu' })).toBeInTheDocument()
    expect(screen.getByText('enduser@example.test')).toBeInTheDocument()

    await user.click(screen.getByRole('menuitem', { name: 'Sign out' }))
    expect(signOut).toHaveBeenCalledOnce()
  })
})
