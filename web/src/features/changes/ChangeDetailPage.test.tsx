import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, expect, test, vi } from 'vitest'
import { changesApi, type Change } from '../../api/changes'
import { monitoringApi, type MaintenanceWindow } from '../../api/monitoring'
import { ChangeDetailPage } from './ChangeDetailPage'

vi.mock('../../api/changes', async (original) => {
  const actual = await original<typeof import('../../api/changes')>()
  return { ...actual, changesApi: { list: vi.fn(), get: vi.fn(), create: vi.fn(), update: vi.fn(), transition: vi.fn() } }
})

vi.mock('../../api/monitoring', async (original) => {
  const actual = await original<typeof import('../../api/monitoring')>()
  return { ...actual, monitoringApi: { listMaintenanceWindows: vi.fn() } }
})

const submitted: Change = {
  id: 'chg-1',
  number: 'CHG-000001',
  title: 'Firmware upgrade on the access switch',
  description: 'The switch reboots twice during the upgrade.',
  status: 'Submitted',
  plannedStartAt: '2026-08-20T09:00:00Z',
  plannedEndAt: '2026-08-20T11:00:00Z',
  includeDependents: true,
  requestedById: 'technician1',
  requestedByName: 'Technician One',
  requestedAt: '2026-08-18T09:00:00Z',
  decidedById: null,
  decidedByName: null,
  decidedAt: null,
  decisionNote: null,
  updatedAt: '2026-08-18T09:00:00Z',
  ciCount: 1,
  dependentCount: 0,
  nextStatuses: ['Approved', 'Rejected', 'Draft', 'Cancelled'],
  cis: [
    {
      ciId: 'ci-1',
      name: 'DC1 access switch',
      type: 'NetworkDevice',
      assetTag: 'AST-0001',
      lifecycleState: 'Deployed',
      isDependent: false,
    },
  ],
}

const approved: Change = {
  ...submitted,
  status: 'Approved',
  decidedById: 'technician2',
  decidedByName: 'Technician Two',
  decidedAt: '2026-08-19T09:00:00Z',
  decisionNote: 'Agreed at CAB.',
  ciCount: 2,
  dependentCount: 1,
  nextStatuses: [],
  cis: [
    ...submitted.cis!,
    {
      ciId: 'ci-2',
      name: 'DC1 backup server',
      type: 'Server',
      assetTag: 'AST-0002',
      lifecycleState: 'Deployed',
      isDependent: true,
    },
  ],
}

const window: MaintenanceWindow = {
  id: 'win-1',
  name: 'CHG-000001 — Firmware upgrade on the access switch',
  description: 'Opened automatically by approved change CHG-000001.',
  startsAt: '2026-08-20T09:00:00Z',
  endsAt: '2026-08-20T11:00:00Z',
  appliesToAllDevices: false,
  deviceIds: ['dev-1', 'dev-2'],
  isActive: true,
  status: 'InProgress',
  createdBy: 'system:change-approval',
  createdAt: '2026-08-19T09:00:00Z',
  updatedBy: 'system:change-approval',
  updatedAt: '2026-08-19T09:00:00Z',
  changeRequestId: 'chg-1',
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={['/changes/chg-1']}>
        <Routes><Route path="/changes/:id" element={<ChangeDetailPage />} /></Routes>
      </MemoryRouter>
    </QueryClientProvider>)
}

beforeEach(() => {
  vi.mocked(changesApi.get).mockResolvedValue(submitted)
  vi.mocked(monitoringApi.listMaintenanceWindows).mockResolvedValue({
    items: [window], total: 1, page: 1, pageSize: 200,
  })
})

afterEach(() => vi.clearAllMocks())

/**
 * The buttons come off `nextStatuses` rather than a copy of the workflow in the browser — WP-5.7 kept
 * one here and left a note that the failure mode is a button that is never offered and nobody reports.
 */
test('the actions offered are the ones the server says are available', async () => {
  renderPage()
  await screen.findByText('Firmware upgrade on the access switch')

  expect(screen.getByRole('button', { name: 'Approve' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Reject' })).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Return to draft' })).toBeInTheDocument()
  // Not offered: a submitted change is already with somebody.
  expect(screen.queryByRole('button', { name: 'Submit for approval' })).not.toBeInTheDocument()
})

test('a finished change offers nothing and says why', async () => {
  vi.mocked(changesApi.get).mockResolvedValue(approved)
  renderPage()
  await screen.findByText('Firmware upgrade on the access switch')

  expect(screen.queryByRole('button', { name: 'Approve' })).not.toBeInTheDocument()
  expect(screen.getByText(/approved and finished/)).toBeInTheDocument()
})

test('approving sends the target and the note', async () => {
  vi.mocked(changesApi.transition).mockResolvedValue(approved)
  renderPage()
  await screen.findByText('Firmware upgrade on the access switch')

  await userEvent.type(screen.getByLabelText('Decision note'), 'Agreed at CAB.')
  await userEvent.click(screen.getByRole('button', { name: 'Approve' }))

  await waitFor(() =>
    expect(changesApi.transition).toHaveBeenCalledWith('chg-1', 'Approved', 'Agreed at CAB.'))
})

/** Somebody agreeing to touch one switch needs to see that it also silences the server behind it. */
test('a CI the dependency walk added is marked as one', async () => {
  vi.mocked(changesApi.get).mockResolvedValue(approved)
  renderPage()
  await screen.findByText('DC1 backup server')

  expect(screen.getByText('1 item + 1 dependent')).toBeInTheDocument()
  expect(screen.getByText('depends on it')).toBeInTheDocument()
})

test('an approved change shows the maintenance window it opened', async () => {
  vi.mocked(changesApi.get).mockResolvedValue(approved)
  renderPage()

  expect(await screen.findByText('Muting now')).toBeInTheDocument()
  expect(screen.getByText('2 devices')).toBeInTheDocument()
})

/**
 * The honest empty state: most of a CMDB is not polled, so a change that opened no window is ordinary
 * rather than broken — and it must not read as "your estate is muted".
 */
test('an approved change covering nothing monitored says no window was opened and why', async () => {
  vi.mocked(changesApi.get).mockResolvedValue(approved)
  vi.mocked(monitoringApi.listMaintenanceWindows).mockResolvedValue({
    items: [], total: 0, page: 1, pageSize: 200,
  })
  renderPage()

  expect(await screen.findByText(/No window was opened/)).toBeInTheDocument()
})

/** WP-2.11's rule: an unreadable answer is not an empty one, and here the difference is safety. */
test('a maintenance window that could not be read is not reported as no window', async () => {
  vi.mocked(changesApi.get).mockResolvedValue(approved)
  vi.mocked(monitoringApi.listMaintenanceWindows).mockRejectedValue(new Error('boom'))
  renderPage()

  expect(await screen.findByText(/whether a window exists is unknown/)).toBeInTheDocument()
  expect(screen.queryByText(/No window was opened/)).not.toBeInTheDocument()
})

/** A draft has no window to look for, so the page must not ask. */
test('a draft does not ask monitoring about a window', async () => {
  vi.mocked(changesApi.get).mockResolvedValue({ ...submitted, status: 'Draft', nextStatuses: ['Submitted', 'Cancelled'] })
  renderPage()
  await screen.findByText('Firmware upgrade on the access switch')

  expect(monitoringApi.listMaintenanceWindows).not.toHaveBeenCalled()
})
