import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, expect, test, vi } from 'vitest'
import { ApiError } from '../../api/client'
import { helpdeskApi, type TicketCategory } from '../../api/helpdesk'
import { SettingsPage } from './SettingsPage'

vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, listCategoriesIncludingInactive: vi.fn() } }
})

function category(id: string, name: string, children: TicketCategory[] = []): TicketCategory {
  return { id, name, parentId: null, isActive: true, sortOrder: 0, fields: [], children }
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><SettingsPage /></MemoryRouter>
    </QueryClientProvider>)
}

afterEach(() => vi.clearAllMocks())

test('the categories section links to its page and counts nested categories', async () => {
  vi.mocked(helpdeskApi.listCategoriesIncludingInactive)
    .mockResolvedValue([category('hardware', 'Hardware', [category('laptops', 'Laptops')]), category('software', 'Software')])

  renderPage()

  const link = screen.getByRole('link', { name: /Ticket categories/ })
  expect(link).toHaveAttribute('href', '/admin/settings/ticket-categories')
  expect(await screen.findByText('3 categories')).toBeInTheDocument()
})

/** The index is a way in, so a count that cannot be read must not stop the link from working. */
test('the section still links through when the count cannot be loaded', () => {
  vi.mocked(helpdeskApi.listCategoriesIncludingInactive).mockRejectedValue(new ApiError(500, 'nope'))

  renderPage()

  expect(screen.getByRole('link', { name: /Ticket categories/ }))
    .toHaveAttribute('href', '/admin/settings/ticket-categories')
})
