import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { contractsApi, type ContractReminderSettings } from '../../api/contracts'
import { RenewalRemindersPage, describeThreshold } from './RenewalRemindersPage'

vi.mock('../../api/contracts', async (original) => {
  const actual = await original<typeof import('../../api/contracts')>()
  return {
    ...actual,
    contractsApi: { ...actual.contractsApi, getReminderSettings: vi.fn(), saveReminderSettings: vi.fn() },
  }
})

const settings = (over: Partial<ContractReminderSettings> = {}): ContractReminderSettings => ({
  thresholdDays: [30, 7, 0], enabled: true, recipients: [], updatedBy: 'default',
  updatedAt: '2026-08-21T00:00:00Z', ...over,
})

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter>
    <QueryClientProvider client={client}><RenewalRemindersPage /></QueryClientProvider>
  </MemoryRouter>)
}

describe('describeThreshold', () => {
  /** Stored in days, offered in months: "3 months before" is how somebody decides this. */
  it('says a month rather than a number of days where one fits', () => {
    expect(describeThreshold(30)).toBe('1 month before')
    expect(describeThreshold(90)).toBe('3 months before')
    expect(describeThreshold(365)).toBe('1 year before')
  })

  it('falls back to days for anything without a name', () => {
    expect(describeThreshold(45)).toBe('45 days before')
  })

  /** Zero is not "0 days before" — it is the day itself, and reads wrong any other way. */
  it('names the expiry day rather than counting to it', () => {
    expect(describeThreshold(0)).toBe('On the day it expires')
  })
})

describe('RenewalRemindersPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(contractsApi.getReminderSettings).mockResolvedValue(settings())
  })

  it('shows the points already set', async () => {
    renderPage()

    const list = await screen.findByRole('list', { name: 'Reminder points' })
    expect(within(list).getByText('1 month before')).toBeInTheDocument()
    expect(within(list).getByText('A week before')).toBeInTheDocument()
    expect(within(list).getByText('On the day it expires')).toBeInTheDocument()
  })

  it('adds a point and saves it widest first', async () => {
    vi.mocked(contractsApi.saveReminderSettings).mockResolvedValue(settings({ thresholdDays: [90, 30, 7, 0] }))

    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: /3 months/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(contractsApi.saveReminderSettings).toHaveBeenCalledWith({
      thresholdDays: [90, 30, 7, 0],
      enabled: true,
      recipients: [],
    }))
  })

  it('removes a point', async () => {
    vi.mocked(contractsApi.saveReminderSettings).mockResolvedValue(settings({ thresholdDays: [30, 0] }))

    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Remove A week before' }))
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(contractsApi.saveReminderSettings).toHaveBeenCalledWith({
      thresholdDays: [30, 0],
      enabled: true,
      recipients: [],
    }))
  })

  it('takes a number of days for anything the presets do not cover', async () => {
    vi.mocked(contractsApi.saveReminderSettings).mockResolvedValue(settings())

    renderPage()
    await userEvent.type(await screen.findByLabelText(/Or a number of days/), '45')
    await userEvent.click(screen.getByRole('button', { name: 'Add' }))

    expect(screen.getByText('45 days before')).toBeInTheDocument()
  })

  /** Off keeps the numbers, so switching back on is not a setup exercise. */
  it('keeps the points when reminders are switched off', async () => {
    vi.mocked(contractsApi.saveReminderSettings).mockResolvedValue(settings({ enabled: false }))

    renderPage()
    await userEvent.click(await screen.findByRole('checkbox', { name: /Send renewal reminders/ }))
    await userEvent.click(screen.getByRole('button', { name: 'Save' }))

    await waitFor(() => expect(contractsApi.saveReminderSettings).toHaveBeenCalledWith({
      thresholdDays: [30, 7, 0],
      enabled: false,
      recipients: [],
    }))
  })

  it('warns rather than saving nothing when every point is removed', async () => {
    vi.mocked(contractsApi.getReminderSettings).mockResolvedValue(settings({ thresholdDays: [30] }))

    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: 'Remove 1 month before' }))

    expect(screen.getByText(/No reminders set/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled()
  })
})
