import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError } from '../../api/client'
import { helpdeskApi } from '../../api/helpdesk'
import { slaApi, type BusinessHoursCalendar, type SlaPolicy } from '../../api/sla'
import { SlaPage } from './SlaPage'

vi.mock('../../api/sla', async (original) => {
  const actual = await original<typeof import('../../api/sla')>()
  return {
    ...actual,
    slaApi: {
      listPolicies: vi.fn(), createPolicy: vi.fn(), updatePolicy: vi.fn(),
      deletePolicy: vi.fn(), reorderPolicies: vi.fn(),
      listCalendars: vi.fn(), createCalendar: vi.fn(), deleteCalendar: vi.fn(),
    },
  }
})

vi.mock('../../api/helpdesk', async (original) => {
  const actual = await original<typeof import('../../api/helpdesk')>()
  return { ...actual, helpdeskApi: { ...actual.helpdeskApi, listCategories: vi.fn() } }
})

const toasts = vi.hoisted(() => ({ success: vi.fn(), error: vi.fn() }))
vi.mock('sonner', () => ({ toast: toasts }))

const calendar: BusinessHoursCalendar = {
  id: 'cal-1', name: 'Business hours', timeZoneId: 'Europe/London',
  workingDays: 31, startTime: '09:00:00', endTime: '17:00:00', policyCount: 2,
}

function policy(overrides: Partial<SlaPolicy> = {}): SlaPolicy {
  return {
    id: 'p1', name: 'Critical', sortOrder: 0, priority: 'Critical', ticketType: null,
    categoryId: null, categoryName: null, responseTargetMinutes: 15, resolutionTargetMinutes: 240,
    warningPercent: 80, calendarId: 'cal-1', calendarName: 'Business hours',
    isActive: true, ticketCount: 0,
    ...overrides,
  }
}

const catchAll = policy({ id: 'p2', name: 'Everything else', sortOrder: 1, priority: null, ticketCount: 12 })

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter><SlaPage /></MemoryRouter>
    </QueryClientProvider>)
}

beforeEach(() => {
  vi.mocked(slaApi.listPolicies).mockResolvedValue([policy(), catchAll])
  vi.mocked(slaApi.listCalendars).mockResolvedValue([calendar])
  vi.mocked(helpdeskApi.listCategories).mockResolvedValue([])
})

afterEach(() => vi.clearAllMocks())

describe('the policy list', () => {
  it('shows the order, the conditions and the targets in readable units', async () => {
    renderPage()

    const row = (await screen.findByText('Critical')).closest('tr')!
    expect(within(row).getByText('1')).toBeInTheDocument()
    expect(within(row).getByText('Critical · Any kind')).toBeInTheDocument()
    expect(within(row).getByText('15m')).toBeInTheDocument()
    expect(within(row).getByText('4h')).toBeInTheDocument()
  })

  /** A catch-all reads as one rather than as blank, since it is the row that swallows everything. */
  it('reads an unconditioned policy as matching anything', async () => {
    renderPage()

    const row = (await screen.findByText('Everything else')).closest('tr')!
    expect(within(row).getByText('Any priority · Any kind')).toBeInTheDocument()
  })

  /** Order is the rule, so the screen has to say so rather than leave it to be discovered. */
  it('says that the first match wins', async () => {
    renderPage()

    await screen.findByText('Critical')

    expect(screen.getByText(/anything below it is never reached/)).toBeInTheDocument()
    expect(screen.getByText(/takes the/)).toBeInTheDocument()
  })

  it('reorders when a row is dropped on another', async () => {
    vi.mocked(slaApi.reorderPolicies).mockResolvedValue([catchAll, policy()])
    renderPage()
    await screen.findByText('Critical')

    fireEvent.dragStart(screen.getByText('Everything else').closest('tr')!)
    fireEvent.dragOver(screen.getByText('Critical').closest('tr')!)
    fireEvent.drop(screen.getByText('Critical').closest('tr')!)

    await waitFor(() => expect(slaApi.reorderPolicies).toHaveBeenCalledWith(['p2', 'p1']))
  })

  /** With no policy at all, a new ticket gets no clock — worth saying, not an empty formality. */
  it('says what an empty list means', async () => {
    vi.mocked(slaApi.listPolicies).mockResolvedValue([])
    renderPage()

    expect(await screen.findByText(/no ticket is measured against anything/i)).toBeInTheDocument()
  })
})

describe('editing a policy', () => {
  it('creates one with the conditions chosen', async () => {
    vi.mocked(slaApi.createPolicy).mockResolvedValue(policy({ id: 'p3', name: 'Network criticals' }))
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('Critical')

    await user.click(screen.getByRole('button', { name: 'New policy' }))
    await user.type(screen.getByLabelText('Name'), 'Network criticals')
    await user.selectOptions(screen.getByLabelText('Priority'), 'Critical')
    await user.selectOptions(screen.getByLabelText('Kind'), 'Incident')
    await user.click(screen.getByRole('button', { name: 'Create policy' }))

    await waitFor(() => expect(slaApi.createPolicy).toHaveBeenCalledWith(expect.objectContaining({
      name: 'Network criticals', priority: 'Critical', ticketType: 'Incident', categoryId: null,
    })))
  })

  /** Conditions default to open, which is what makes a new policy a catch-all until narrowed. */
  it('leaves every condition open by default', async () => {
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('Critical')

    await user.click(screen.getByRole('button', { name: 'New policy' }))

    expect(screen.getByLabelText('Priority')).toHaveValue('')
    expect(screen.getByLabelText('Kind')).toHaveValue('')
    expect(screen.getByLabelText('Category')).toHaveValue('')
  })

  /**
   * The behaviour people most need warning about: an edit does not move a clock that is already
   * running, and the screen says so where the edit happens.
   */
  it('says that an edit reaches new tickets only, and how many are already running', async () => {
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('Everything else')

    await user.click(screen.getByRole('button', { name: 'Edit Everything else' }))

    expect(screen.getByText(/12 tickets have run against this policy/)).toBeInTheDocument()
    expect(screen.getByText(/this edit applies to new tickets/)).toBeInTheDocument()
  })

  /** Resolution sooner than response is refused by the server; the form should not get that far. */
  it('will not submit a resolution target sooner than the response target', async () => {
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('Critical')

    await user.click(screen.getByRole('button', { name: 'Edit Critical' }))
    await user.clear(screen.getByLabelText('Resolution (minutes)'))
    await user.type(screen.getByLabelText('Resolution (minutes)'), '5')

    expect(screen.getByRole('alert')).toHaveTextContent('Resolution cannot be sooner than response')
    expect(screen.getByRole('button', { name: 'Save policy' })).toBeDisabled()
  })

  /** FAILURE PATH: a policy tickets have run against stays, and the refusal says what to do instead. */
  it('reports the server refusing to delete a policy in use', async () => {
    vi.mocked(slaApi.deletePolicy).mockRejectedValue(new ApiError(409,
      'Tickets have already run against it; deactivate it instead so their clocks stay explainable.'))
    vi.spyOn(window, 'confirm').mockReturnValue(true)
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('Everything else')

    await user.click(screen.getByRole('button', { name: 'Delete Everything else' }))

    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith(
      'Tickets have already run against it; deactivate it instead so their clocks stay explainable.'))
    expect(screen.getByText('Everything else')).toBeInTheDocument()
  })
})

describe('business hours', () => {
  it('lists a calendar with its days and hours', async () => {
    renderPage()

    // "Business hours" is also the section heading, so the row is found by a cell only it has.
    const row = (await screen.findByText('Europe/London')).closest('tr')!
    expect(within(row).getByText('Mon Tue Wed Thu Fri')).toBeInTheDocument()
    expect(within(row).getByText('09:00–17:00')).toBeInTheDocument()
    expect(within(row).getByText('2 policies')).toBeInTheDocument()
  })

  /** Hours are read live by every running clock, so editing one would move tickets already going. */
  it('explains why a calendar cannot be edited', async () => {
    renderPage()
    await screen.findByText('Business hours')

    expect(screen.getByText(/cannot be edited/)).toBeInTheDocument()
  })

  it('creates a calendar from the days picked', async () => {
    vi.mocked(slaApi.createCalendar).mockResolvedValue(calendar)
    const user = userEvent.setup()
    renderPage()
    await screen.findByText('Business hours')

    await user.click(screen.getByRole('button', { name: 'New calendar' }))
    await user.type(screen.getByLabelText('Name'), '24x7')
    await user.click(screen.getByRole('button', { name: 'Sat' }))
    await user.click(screen.getByRole('button', { name: 'Sun' }))
    await user.click(screen.getByRole('button', { name: 'Create calendar' }))

    await waitFor(() => expect(slaApi.createCalendar).toHaveBeenCalledWith(expect.objectContaining({
      name: '24x7', workingDays: 127,
    })))
  })
})
