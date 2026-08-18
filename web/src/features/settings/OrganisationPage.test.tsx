import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, expect, test, vi } from 'vitest'
import { ApiError } from '../../api/client'
import { directoryApi, type AdminDepartment, type AdminSite } from '../../api/directory'
import { OrganisationPage } from './OrganisationPage'

vi.mock('../../api/directory', async (original) => {
  const actual = await original<typeof import('../../api/directory')>()
  return {
    ...actual,
    directoryApi: {
      ...actual.directoryApi,
      listAdminDepartments: vi.fn(),
      listAdminSites: vi.fn(),
      createDepartment: vi.fn(),
      updateDepartment: vi.fn(),
      deleteDepartment: vi.fn(),
      createSite: vi.fn(),
      updateSite: vi.fn(),
      deleteSite: vi.fn(),
    },
  }
})

const toasts = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }))
vi.mock('sonner', () => ({ toast: toasts }))

const headOffice = { id: 'hq', code: 'HQ', name: 'Head Office' }
const dataCentre = { id: 'dc1', code: 'DC1', name: 'Primary Data Centre' }

const it: AdminDepartment = { id: 'it', code: 'IT', name: 'Information Technology', sites: [headOffice, dataCentre], userCount: 6 }
const finance: AdminDepartment = { id: 'fin', code: 'FIN', name: 'Finance', sites: [headOffice], userCount: 3 }

const hqSite: AdminSite = { id: 'hq', code: 'HQ', name: 'Head Office', departments: [{ id: 'it', code: 'IT', name: 'Information Technology' }], userCount: 9 }
// Both sites are listed, because the department dialog builds its options from the locations list —
// a link to a location missing from it would have nothing to tick.
const dcSite: AdminSite = { id: 'dc1', code: 'DC1', name: 'Primary Data Centre', departments: [{ id: 'it', code: 'IT', name: 'Information Technology' }], userCount: 4 }

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><OrganisationPage /></MemoryRouter>
    </QueryClientProvider>)
}

afterEach(() => vi.clearAllMocks())

function seed() {
  vi.mocked(directoryApi.listAdminDepartments).mockResolvedValue([it, finance])
  vi.mocked(directoryApi.listAdminSites).mockResolvedValue([hqSite, dcSite])
}

/** The many-to-many is the whole point, so a department at two locations must show both. */
test('departments list every location they operate at', async () => {
  seed()

  renderPage()

  const row = (await screen.findByRole('cell', { name: 'Information Technology' })).closest('tr')!
  expect(within(row).getByText('Head Office')).toBeInTheDocument()
  expect(within(row).getByText('Primary Data Centre')).toBeInTheDocument()
})

test('a new department is created with the locations ticked', async () => {
  seed()
  vi.mocked(directoryApi.createDepartment).mockResolvedValue({ ...finance, id: 'new', code: 'OPS', name: 'Operations' })
  const user = userEvent.setup()

  renderPage()
  await user.click(await screen.findByRole('button', { name: 'New department' }))
  await user.type(screen.getByLabelText('Code'), 'OPS')
  await user.type(screen.getByLabelText('Name'), 'Operations')
  await user.click(screen.getByRole('checkbox', { name: 'Head Office' }))
  await user.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(directoryApi.createDepartment)
    .toHaveBeenCalledWith({ code: 'OPS', name: 'Operations', siteIds: ['hq'] }))
})

/** Editing from the locations tab writes the same link rows, just from the other end. */
test('a location can be edited to set which departments are present there', async () => {
  seed()
  vi.mocked(directoryApi.updateSite).mockResolvedValue(hqSite)
  const user = userEvent.setup()

  renderPage()
  await user.click(await screen.findByRole('tab', { name: 'Locations' }))
  await user.click(await screen.findByRole('button', { name: 'Edit Head Office' }))

  // Arrives with the existing link already ticked, then Finance is added alongside it.
  expect(screen.getByRole('checkbox', { name: 'Information Technology' })).toBeChecked()
  await user.click(screen.getByRole('checkbox', { name: 'Finance' }))
  await user.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(directoryApi.updateSite)
    .toHaveBeenCalledWith('hq', { code: 'HQ', name: 'Head Office', departmentIds: ['it', 'fin'] }))
})

test('unticking a location removes it from the department', async () => {
  seed()
  vi.mocked(directoryApi.updateDepartment).mockResolvedValue(it)
  const user = userEvent.setup()

  renderPage()
  await user.click(await screen.findByRole('button', { name: 'Edit Information Technology' }))
  await user.click(screen.getByRole('checkbox', { name: 'Primary Data Centre' }))
  await user.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(directoryApi.updateDepartment)
    .toHaveBeenCalledWith('it', { code: 'IT', name: 'Information Technology', siteIds: ['hq'] }))
})

/** FAILURE PATH: people or assets still assigned. The server refuses; the row must stay. */
test('a department still in use reports the refusal and is not removed', async () => {
  seed()
  vi.mocked(directoryApi.deleteDepartment).mockRejectedValue(new ApiError(409,
    'People or configuration items still belong to this department; move them first.'))
  vi.spyOn(window, 'confirm').mockReturnValue(true)
  const user = userEvent.setup()

  renderPage()
  await user.click(await screen.findByRole('button', { name: 'Delete Information Technology' }))

  await waitFor(() => expect(toasts.error)
    .toHaveBeenCalledWith('People or configuration items still belong to this department; move them first.'))
  expect(screen.getByRole('cell', { name: 'Information Technology' })).toBeInTheDocument()
})

/** FAILURE PATH: a duplicate code belongs beside the form, not in a toast. */
test('a duplicate code keeps the dialog open with the reason in it', async () => {
  seed()
  vi.mocked(directoryApi.createDepartment).mockRejectedValue(new ApiError(409, "A department with code 'IT' already exists."))
  const user = userEvent.setup()

  renderPage()
  await user.click(await screen.findByRole('button', { name: 'New department' }))
  await user.type(screen.getByLabelText('Code'), 'IT')
  await user.type(screen.getByLabelText('Name'), 'Information Technology')
  await user.click(screen.getByRole('button', { name: 'Save' }))

  expect(await screen.findByRole('alert')).toHaveTextContent("A department with code 'IT' already exists.")
  expect(screen.getByRole('dialog')).toBeInTheDocument()
})

test('a failed load offers a retry rather than looking empty', async () => {
  vi.mocked(directoryApi.listAdminDepartments).mockRejectedValue(new ApiError(500, 'nope'))
  vi.mocked(directoryApi.listAdminSites).mockResolvedValue([])

  renderPage()

  expect(await screen.findByText('Departments could not be loaded')).toBeInTheDocument()
  expect(screen.queryByText('No departments yet')).not.toBeInTheDocument()
})

/**
 * Creating a location has to reach the pickers elsewhere in the app, and they do not all share a key
 * shape: most are ['directory', …] but the drift report and audit sessions use a flat
 * ['directory-sites'], which a prefix match on the first would miss entirely.
 */
test('writing a location invalidates both directory key shapes the rest of the app uses', async () => {
  seed()
  vi.mocked(directoryApi.createSite).mockResolvedValue(hqSite)
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  client.setQueryData(['directory', 'sites'], [])
  client.setQueryData(['directory-sites'], [])
  const user = userEvent.setup()

  render(
    <QueryClientProvider client={client}>
      <MemoryRouter><OrganisationPage /></MemoryRouter>
    </QueryClientProvider>)

  await user.click(await screen.findByRole('tab', { name: 'Locations' }))
  await user.click(screen.getByRole('button', { name: 'New location' }))
  await user.type(screen.getByLabelText('Code'), 'BR2')
  await user.type(screen.getByLabelText('Name'), 'Second Branch')
  await user.click(screen.getByRole('button', { name: 'Save' }))

  await waitFor(() => expect(client.getQueryState(['directory', 'sites'])?.isInvalidated).toBe(true))
  expect(client.getQueryState(['directory-sites'])?.isInvalidated).toBe(true)
})
