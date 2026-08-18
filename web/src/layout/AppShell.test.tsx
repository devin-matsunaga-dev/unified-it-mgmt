import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Link, MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { AppShell } from './AppShell'
import { usePageHeading } from './pageHeading'

/**
 * The shell needs a query client since WP-5.4 put the global search bar in its topbar. Nothing here types
 * into it, so the client never fetches — GlobalSearch.test.tsx is where the bar itself is exercised.
 */
function renderShell(path = '/') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<MemoryRouter initialEntries={[path]}><QueryClientProvider client={client}><AppShell /></QueryClientProvider></MemoryRouter>)
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

describe('hiding the navigation', () => {
  beforeEach(() => localStorage.clear())

  it('hides the sidebar and reclaims the space, then puts it back', async () => {
    const user = userEvent.setup()
    renderShell()

    const nav = screen.getByRole('navigation', { name: 'Primary navigation' })
    const aside = nav.closest('aside')!
    expect(aside.className).toContain('lg:translate-x-0')

    await user.click(screen.getByRole('button', { name: 'Hide navigation' }))

    expect(aside.className).toContain('lg:-translate-x-full')
    // The content area takes the room back rather than leaving a gap where the sidebar was.
    expect(document.querySelector('main')!.className).toContain('lg:ml-0')

    await user.click(screen.getByRole('button', { name: 'Show navigation' }))
    expect(aside.className).toContain('lg:translate-x-0')
  })

  /**
   * A sidebar translated off screen is still in the tab order, so a keyboard would otherwise walk
   * into navigation nobody can see.
   */
  it('takes the hidden sidebar out of the accessibility tree', async () => {
    const user = userEvent.setup()
    renderShell()

    await user.click(screen.getByRole('button', { name: 'Hide navigation' }))

    expect(screen.getByRole('navigation', { name: 'Primary navigation', hidden: true }).closest('aside'))
      .toHaveAttribute('aria-hidden', 'true')
  })

  it('remembers the choice across a remount', async () => {
    const user = userEvent.setup()
    const first = renderShell()
    await user.click(screen.getByRole('button', { name: 'Hide navigation' }))
    first.unmount()

    renderShell()

    expect(screen.getByRole('button', { name: 'Show navigation' })).toBeInTheDocument()
  })

  /** Full screen is where the room is wanted most, so it collapses on the way in. */
  it('collapses when the browser goes full screen and restores on the way out', async () => {
    renderShell()
    expect(screen.getByRole('button', { name: 'Hide navigation' })).toBeInTheDocument()

    await act(async () => {
      Object.defineProperty(document, 'fullscreenElement', { value: document.body, configurable: true })
      document.dispatchEvent(new Event('fullscreenchange'))
    })
    expect(screen.getByRole('button', { name: 'Show navigation' })).toBeInTheDocument()

    await act(async () => {
      Object.defineProperty(document, 'fullscreenElement', { value: null, configurable: true })
      document.dispatchEvent(new Event('fullscreenchange'))
    })
    expect(screen.getByRole('button', { name: 'Hide navigation' })).toBeInTheDocument()
  })

  /**
   * A collapse that only happened because of full screen must not become the answer somebody gets on
   * their next ordinary visit.
   */
  it('does not store a collapse that full screen caused', async () => {
    renderShell()

    await act(async () => {
      Object.defineProperty(document, 'fullscreenElement', { value: document.body, configurable: true })
      document.dispatchEvent(new Event('fullscreenchange'))
    })

    expect(localStorage.getItem('shell:nav-collapsed')).not.toBe('true')

    await act(async () => {
      Object.defineProperty(document, 'fullscreenElement', { value: null, configurable: true })
      document.dispatchEvent(new Event('fullscreenchange'))
    })
  })

  /** Below lg the drawer already has its own button; a second control would be two ways to do one job. */
  it('does not offer the desktop toggle on small screens', () => {
    renderShell()

    expect(screen.getByRole('button', { name: 'Hide navigation' }).className).toContain('lg:inline-flex')
    expect(screen.getByRole('button', { name: 'Hide navigation' }).className).toContain('hidden')
  })
})

describe('the page title in the topbar', () => {
  it('names the route being viewed, not the section it hangs under', () => {
    renderShell('/assets')

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Assets')
    expect(screen.getByText('The configuration items every ticket, alert, and device links back to.')).toBeInTheDocument()
  })

  /** The old topbar matched on prefix, so every unlisted route answered "Overview". */
  it('does not fall back to the overview heading on a route of its own', () => {
    renderShell('/admin/settings/sla')

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Service levels')
  })

  /**
   * The page is the only thing that knows a record's name, so it overrides the route's heading — and
   * hands it back when it unmounts, or the next page would wear the last record's title.
   */
  it('lets the page on screen override the heading, and takes it back when the page goes', async () => {
    const user = userEvent.setup()
    function CiPage() {
      usePageHeading({ title: 'Branch switch' })
      return <Link to="/assets">Back to assets</Link>
    }
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
    render(<MemoryRouter initialEntries={['/assets/1']}>
      <QueryClientProvider client={client}>
        <Routes>
          <Route element={<AppShell />}>
            <Route path="assets" element={<p>The list</p>} />
            <Route path="assets/:id" element={<CiPage />} />
          </Route>
        </Routes>
      </QueryClientProvider>
    </MemoryRouter>)

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Branch switch')

    await user.click(screen.getByRole('link', { name: 'Back to assets' }))

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Assets')
  })
})
