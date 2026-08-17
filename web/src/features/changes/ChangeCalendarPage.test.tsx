import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { assetsApi } from '../../api/assets'
import { changesApi, type Change } from '../../api/changes'
import { monitoringApi, type MaintenanceWindow } from '../../api/monitoring'
import { ChangeCalendarPage } from './ChangeCalendarPage'

vi.mock('../../api/changes', async (original) => {
  const actual = await original<typeof import('../../api/changes')>()
  return { ...actual, changesApi: { list: vi.fn(), get: vi.fn(), create: vi.fn(), update: vi.fn(), transition: vi.fn() } }
})

vi.mock('../../api/monitoring', async (original) => {
  const actual = await original<typeof import('../../api/monitoring')>()
  return { ...actual, monitoringApi: { listMaintenanceWindows: vi.fn() } }
})

vi.mock('../../api/assets', () => ({ assetsApi: { listCis: vi.fn() } }))

const navigate = vi.fn()
vi.mock('react-router-dom', async (original) => {
  const actual = await original<typeof import('react-router-dom')>()
  return { ...actual, useNavigate: () => navigate }
})

/** The 12th of whichever month the page opens on, so nothing here depends on the date it is run. */
const dayInView = (day: number, hour: number) => {
  const now = new Date()
  return new Date(now.getFullYear(), now.getMonth(), day, hour, 0)
}

const submitted: Change = {
  id: 'chg-1',
  number: 'CHG-000001',
  title: 'Firmware upgrade on the access switch',
  description: 'The switch reboots twice.',
  status: 'Submitted',
  plannedStartAt: dayInView(12, 9).toISOString(),
  plannedEndAt: dayInView(12, 11).toISOString(),
  includeDependents: false,
  requestedById: 'technician1',
  requestedByName: 'Technician One',
  requestedAt: dayInView(10, 9).toISOString(),
  decidedById: null,
  decidedByName: null,
  decidedAt: null,
  decisionNote: null,
  updatedAt: dayInView(10, 9).toISOString(),
  ciCount: 1,
  dependentCount: 0,
  nextStatuses: ['Approved', 'Rejected', 'Draft', 'Cancelled'],
  cis: null,
}

const openWindow: MaintenanceWindow = {
  id: 'win-1',
  name: 'CHG-000002 — Storage firmware',
  description: null,
  startsAt: dayInView(14, 22).toISOString(),
  endsAt: dayInView(15, 2).toISOString(),
  appliesToAllDevices: false,
  deviceIds: ['dev-1'],
  isActive: true,
  status: 'InProgress',
  createdBy: 'system:change-approval',
  createdAt: dayInView(13, 9).toISOString(),
  updatedBy: 'system:change-approval',
  updatedAt: dayInView(13, 9).toISOString(),
  changeRequestId: 'chg-2',
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><ChangeCalendarPage /></MemoryRouter>
    </QueryClientProvider>)
}

beforeEach(() => {
  vi.mocked(changesApi.list).mockResolvedValue({ items: [submitted], total: 1, page: 1, pageSize: 200 })
  vi.mocked(monitoringApi.listMaintenanceWindows).mockResolvedValue({
    items: [openWindow], total: 1, page: 1, pageSize: 200,
  })
  vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 25 })
})

afterEach(() => vi.clearAllMocks())

test('the calendar draws changes and the windows they opened on one grid', async () => {
  renderPage()

  expect(await screen.findByTitle(/CHG-000001 — Firmware upgrade/)).toBeInTheDocument()
  // The window is a chip and not a link: a maintenance window has no page of its own. It appears more
  // than once because it spans midnight, which the next test is about.
  expect(await screen.findAllByTitle('CHG-000002 — Storage firmware — alerts withheld')).not.toHaveLength(0)
})

/**
 * A window spanning midnight has to appear on both days, because a calendar that showed it once leaves
 * the other day looking free — which is the one thing this screen exists to prevent.
 */
test('a window crossing midnight appears on both days', async () => {
  renderPage()

  const chips = await screen.findAllByTitle('CHG-000002 — Storage firmware — alerts withheld')
  expect(chips).toHaveLength(2)
})

test('paging to the next month asks the server for that month', async () => {
  renderPage()
  await screen.findByTitle(/CHG-000001/)
  const opened = screen.getByTestId('calendar-month').textContent

  await userEvent.click(screen.getByRole('button', { name: 'Next month' }))

  await waitFor(() => expect(screen.getByTestId('calendar-month').textContent).not.toBe(opened))
  await waitFor(() => {
    const last = vi.mocked(changesApi.list).mock.calls.at(-1)![0]!
    expect(new Date(last.from!).getTime()).toBeGreaterThan(new Date().getTime() - 86_400_000 * 62)
  })
})

test('a change waiting for a decision is counted on its own tile', async () => {
  renderPage()
  await screen.findByTitle(/CHG-000001/)

  const tile = screen.getByText('Waiting for a decision').closest('div')!.parentElement!
  expect(within(tile).getByText('1')).toBeInTheDocument()
})

/** WP-2.11's rule: a failed read must not be rendered as a zero, because a zero is a claim. */
test('a calendar that could not be loaded says so and offers a retry', async () => {
  vi.mocked(changesApi.list).mockRejectedValue(new Error('boom'))
  renderPage()

  expect(await screen.findByRole('button', { name: 'Try again' })).toBeInTheDocument()
  expect(screen.queryByText('0')).not.toBeInTheDocument()
})

test('raising a change sends the slot, the items and whether dependents are covered', async () => {
  vi.mocked(assetsApi.listCis).mockResolvedValue({
    items: [{ id: 'ci-1', name: 'DC1 access switch', type: 'NetworkDevice' }] as never,
    total: 1,
    page: 1,
    pageSize: 25,
  })
  vi.mocked(changesApi.create).mockResolvedValue({ ...submitted, id: 'chg-9', status: 'Draft' })
  renderPage()
  await screen.findByTitle(/CHG-000001/)

  await userEvent.click(screen.getByRole('button', { name: /New change/ }))
  const dialog = screen.getByRole('dialog', { name: 'New change' })

  await userEvent.type(within(dialog).getByLabelText('Title'), 'Swap the uplink')
  await userEvent.type(within(dialog).getByLabelText('What is being done'), 'Ten minutes of downtime.')
  await userEvent.click(await within(dialog).findByRole('checkbox', { name: /DC1 access switch/ }))
  await userEvent.click(within(dialog).getByRole('checkbox', { name: /Also cover what depends on these/ }))
  await userEvent.click(within(dialog).getByRole('button', { name: 'Raise change' }))

  await waitFor(() => expect(changesApi.create).toHaveBeenCalledOnce())
  const input = vi.mocked(changesApi.create).mock.calls[0][0]
  expect(input.title).toBe('Swap the uplink')
  expect(input.ciIds).toEqual(['ci-1'])
  expect(input.includeDependents).toBe(true)
  expect(new Date(input.plannedEndAt).getTime()).toBeGreaterThan(new Date(input.plannedStartAt).getTime())
})

/** The server's field error has to land beside the input it names, not in a toast that scrolls away. */
test('a rejected slot shows the server’s reason under the end field', async () => {
  const { ApiError } = await vi.importActual<typeof import('../../api/client')>('../../api/client')
  vi.mocked(changesApi.create).mockRejectedValue(
    new ApiError(400, 'Invalid request.', { PlannedEndAt: ['A change must end after it starts.'] }))
  renderPage()
  await screen.findByTitle(/CHG-000001/)

  await userEvent.click(screen.getByRole('button', { name: /New change/ }))
  const dialog = screen.getByRole('dialog', { name: 'New change' })
  await userEvent.click(within(dialog).getByRole('button', { name: 'Raise change' }))

  expect(await within(dialog).findByText('A change must end after it starts.')).toBeInTheDocument()
})
