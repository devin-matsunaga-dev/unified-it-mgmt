import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, expect, test, vi } from 'vitest'
import { ApiError } from '../../api/client'
import { helpdeskApi, type TicketCategory } from '../../api/helpdesk'
import { TicketCategoriesPage } from './TicketCategoriesPage'

vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return {
    ...actual,
    helpdeskApi: {
      ...actual.helpdeskApi,
      listCategoriesIncludingInactive: vi.fn(),
      createCategory: vi.fn(),
      updateCategory: vi.fn(),
      deleteCategory: vi.fn(),
    },
  }
})

const toasts = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }))
vi.mock('sonner', () => ({ toast: toasts }))

function category(id: string, name: string, overrides: Partial<TicketCategory> = {}): TicketCategory {
  return { id, name, parentId: null, isActive: true, sortOrder: 0, fields: [], children: [], ...overrides }
}

const hardware = category('hardware', 'Hardware', {
  children: [category('laptops', 'Laptops', { parentId: 'hardware', sortOrder: 1 })],
})
const retired = category('retired', 'Fax machines', { isActive: false })

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><TicketCategoriesPage /></MemoryRouter>
    </QueryClientProvider>)
}

afterEach(() => vi.clearAllMocks())

/** The admin view is the only one that shows deactivated categories, so both states have to be here. */
test('the tree lists nested categories in order and marks inactive ones', async () => {
  vi.mocked(helpdeskApi.listCategoriesIncludingInactive).mockResolvedValue([hardware, retired])

  renderPage()

  const rows = await screen.findAllByRole('row')
  expect(rows.slice(1).map((row) => within(row).getAllByRole('cell')[0].textContent))
    .toEqual(['Hardware', 'Laptops', 'Fax machines'])
  expect(within(rows[3]).getByText('Inactive')).toBeInTheDocument()
  expect(within(rows[1]).getByText('Active')).toBeInTheDocument()
})

test('a new top-level category is created with the name and sort order given', async () => {
  vi.mocked(helpdeskApi.listCategoriesIncludingInactive).mockResolvedValue([hardware])
  vi.mocked(helpdeskApi.createCategory).mockResolvedValue(category('network', 'Network'))
  const user = userEvent.setup()

  renderPage()
  await user.click(await screen.findByRole('button', { name: 'New category' }))
  await user.type(screen.getByLabelText('Name'), 'Network')
  await user.clear(screen.getByLabelText('Sort order'))
  await user.type(screen.getByLabelText('Sort order'), '5')
  await user.click(screen.getByRole('button', { name: 'Create category' }))

  await waitFor(() => expect(helpdeskApi.createCategory).toHaveBeenCalledWith({ name: 'Network', parentId: null, sortOrder: 5 }))
  expect(toasts.success).toHaveBeenCalledWith('Network created')
})

/** "Add child" is the whole reason the tree is worth having, so it must pre-select the parent. */
test('add child opens the dialog with that category already chosen as the parent', async () => {
  vi.mocked(helpdeskApi.listCategoriesIncludingInactive).mockResolvedValue([hardware])
  vi.mocked(helpdeskApi.createCategory).mockResolvedValue(category('docks', 'Docks', { parentId: 'hardware' }))
  const user = userEvent.setup()

  renderPage()
  await user.click(await screen.findByRole('button', { name: 'Add a category under Hardware' }))

  expect(screen.getByLabelText('Parent category')).toHaveValue('hardware')

  await user.type(screen.getByLabelText('Name'), 'Docks')
  await user.click(screen.getByRole('button', { name: 'Create category' }))

  await waitFor(() => expect(helpdeskApi.createCategory).toHaveBeenCalledWith({ name: 'Docks', parentId: 'hardware', sortOrder: 0 }))
})

test('editing a category can deactivate it without touching its name', async () => {
  vi.mocked(helpdeskApi.listCategoriesIncludingInactive).mockResolvedValue([hardware])
  vi.mocked(helpdeskApi.updateCategory).mockResolvedValue({ ...hardware, isActive: false })
  const user = userEvent.setup()

  renderPage()
  await user.click(await screen.findByRole('button', { name: 'Edit Hardware' }))
  await user.click(screen.getByLabelText('Active'))
  await user.click(screen.getByRole('button', { name: 'Save category' }))

  await waitFor(() => expect(helpdeskApi.updateCategory)
    .toHaveBeenCalledWith('hardware', { name: 'Hardware', parentId: null, isActive: false, sortOrder: 0 }))
})

/** The server refuses a cycle; the picker should never have offered one in the first place. */
test('the parent picker offers neither the category being edited nor its children', async () => {
  vi.mocked(helpdeskApi.listCategoriesIncludingInactive).mockResolvedValue([hardware, retired])
  const user = userEvent.setup()

  renderPage()
  await user.click(await screen.findByRole('button', { name: 'Edit Hardware' }))

  const options = within(screen.getByLabelText('Parent category')).getAllByRole('option')
  expect(options.map((option) => option.textContent)).toEqual(['None — top level', 'Fax machines'])
})

/** FAILURE PATH: a category with tickets under it comes back 409 and must not look like a success. */
test('a category that is still in use reports the server refusal and stays in the list', async () => {
  vi.mocked(helpdeskApi.listCategoriesIncludingInactive).mockResolvedValue([hardware])
  vi.mocked(helpdeskApi.deleteCategory).mockRejectedValue(new ApiError(409,
    'Categories with child categories or tickets cannot be deleted; deactivate it instead.'))
  vi.spyOn(window, 'confirm').mockReturnValue(true)
  const user = userEvent.setup()

  renderPage()
  await user.click(await screen.findByRole('button', { name: 'Delete Hardware' }))

  await waitFor(() => expect(toasts.error)
    .toHaveBeenCalledWith('Categories with child categories or tickets cannot be deleted; deactivate it instead.'))
  expect(toasts.success).not.toHaveBeenCalled()
  expect(screen.getByRole('cell', { name: 'Hardware' })).toBeInTheDocument()
})

/** FAILURE PATH: a duplicate name is a 409 that belongs beside the form, not in a toast. */
test('a duplicate name keeps the dialog open and shows the reason in it', async () => {
  vi.mocked(helpdeskApi.listCategoriesIncludingInactive).mockResolvedValue([hardware])
  vi.mocked(helpdeskApi.createCategory).mockRejectedValue(new ApiError(409, "A category named 'Hardware' already exists here."))
  const user = userEvent.setup()

  renderPage()
  await user.click(await screen.findByRole('button', { name: 'New category' }))
  await user.type(screen.getByLabelText('Name'), 'Hardware')
  await user.click(screen.getByRole('button', { name: 'Create category' }))

  expect(await screen.findByRole('alert')).toHaveTextContent("A category named 'Hardware' already exists here.")
  expect(screen.getByRole('dialog')).toBeInTheDocument()
})

/** FAILURE PATH: the list failing to load must offer a way back, not an empty state that reads as "none". */
test('a failed load shows an error with a retry rather than the empty state', async () => {
  vi.mocked(helpdeskApi.listCategoriesIncludingInactive).mockRejectedValue(new ApiError(500, 'The request could not be completed.'))

  renderPage()

  expect(await screen.findByText('Categories could not be loaded')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  expect(screen.queryByText('No categories yet')).not.toBeInTheDocument()
})

test('inactive categories can be hidden from the tree', async () => {
  vi.mocked(helpdeskApi.listCategoriesIncludingInactive).mockResolvedValue([hardware, retired])
  const user = userEvent.setup()

  renderPage()
  await user.click(await screen.findByLabelText('Show inactive (1)'))

  expect(screen.queryByRole('cell', { name: 'Fax machines' })).not.toBeInTheDocument()
  expect(screen.getByRole('cell', { name: 'Hardware' })).toBeInTheDocument()
})

/**
 * The whole point of the screen: a category created in Settings has to reach the pickers on the
 * ticket screen. Those read ['ticket-categories'] while this page reads ['ticket-categories','all'],
 * so this asserts the invalidation prefix-matches BOTH and not only its own key.
 */
test('creating a category invalidates the key the ticket screen picker reads', async () => {
  vi.mocked(helpdeskApi.listCategoriesIncludingInactive).mockResolvedValue([hardware])
  vi.mocked(helpdeskApi.createCategory).mockResolvedValue(category('network', 'Network'))
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  // Seed the exact query TicketListPage and QuickCreateTicket share, as if that page had already run.
  client.setQueryData(['ticket-categories'], [hardware])
  const user = userEvent.setup()

  render(
    <QueryClientProvider client={client}>
      <MemoryRouter><TicketCategoriesPage /></MemoryRouter>
    </QueryClientProvider>)

  await user.click(await screen.findByRole('button', { name: 'New category' }))
  await user.type(screen.getByLabelText('Name'), 'Network')
  await user.click(screen.getByRole('button', { name: 'Create category' }))

  // The ticket screen's query has no observer mounted here, so the invalidation mark stays put.
  await waitFor(() => expect(client.getQueryState(['ticket-categories'])?.isInvalidated).toBe(true))
  // This page's own query IS mounted, so invalidating it refetched rather than leaving a mark.
  expect(helpdeskApi.listCategoriesIncludingInactive).toHaveBeenCalledTimes(2)
})
