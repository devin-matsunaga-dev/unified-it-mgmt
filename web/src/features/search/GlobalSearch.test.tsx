import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { searchApi, type SearchResults } from '../../api/search'
import { GlobalSearch } from './GlobalSearch'

vi.mock('../../api/search', async (original) => {
  const actual = await original<typeof import('../../api/search')>()
  return { ...actual, searchApi: { ...actual.searchApi, search: vi.fn() } }
})

const results: SearchResults = {
  term: 'core',
  limit: 5,
  types: ['Ticket', 'Ci', 'Device', 'Alert', 'User'],
  summary: { returnedCount: 5, totalCount: 48, truncated: true },
  groups: [
    {
      type: 'Ticket',
      status: 'Searched',
      returned: 2,
      total: 41,
      truncated: true,
      hits: [
        {
          type: 'Ticket', id: 'ticket-1', title: 'Core switch is unreachable',
          reference: 'INC-000042', subtitle: 'Marion Halstead', badge: 'InProgress',
        },
        {
          type: 'Ticket', id: 'ticket-2', title: 'Core switch port flapping',
          reference: 'INC-000043', subtitle: 'Sam Elsewhere', badge: 'Resolved',
        },
      ],
    },
    {
      type: 'Ci',
      status: 'Searched',
      returned: 1,
      total: 1,
      truncated: false,
      hits: [
        {
          type: 'Ci', id: 'ci-1', title: 'DC1 core switch A',
          reference: 'NET-0002', subtitle: 'Data centre 1', badge: 'Deployed',
        },
      ],
    },
    { type: 'Device', status: 'Searched', returned: 0, total: 0, truncated: false, hits: [] },
    {
      type: 'Alert',
      status: 'Searched',
      returned: 1,
      total: 5,
      truncated: true,
      hits: [
        {
          type: 'Alert', id: 'alert-1', title: 'CPU above 90%',
          reference: null, subtitle: '10.10.0.2', badge: 'Critical',
        },
      ],
    },
    {
      type: 'User',
      status: 'Searched',
      returned: 1,
      total: 1,
      truncated: false,
      hits: [
        {
          type: 'User', id: 'user-1', title: 'Marion Halstead',
          reference: 'mhalstead', subtitle: 'Finance · Head Office', badge: 'EndUser',
        },
      ],
    },
  ],
}

const empty: SearchResults = {
  term: 'qwlkjhasdf',
  limit: 5,
  types: ['Ticket', 'Ci', 'Device', 'Alert', 'User'],
  summary: { returnedCount: 0, totalCount: 0, truncated: false },
  groups: (['Ticket', 'Ci', 'Device', 'Alert', 'User'] as const).map((type) => ({
    type, status: 'Searched' as const, returned: 0, total: 0, truncated: false, hits: [],
  })),
}

/** Renders the bar with a landing page per route, so "it navigated" is observable rather than mocked. */
function renderSearch() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<MemoryRouter initialEntries={['/']}><QueryClientProvider client={client}>
    <GlobalSearch />
    <Routes>
      <Route path="/" element={<p>Overview</p>} />
      <Route path="/tickets/:id" element={<p>Ticket page</p>} />
      <Route path="/assets/:id" element={<p>Asset page</p>} />
      <Route path="/monitoring/alerts" element={<p>Alert board</p>} />
    </Routes>
  </QueryClientProvider></MemoryRouter>)
}

/** Renders the bar and types into it, which is the opening move of every test here. */
async function type(term: string) {
  renderSearch()
  const user = userEvent.setup()
  await user.type(screen.getByRole('combobox', { name: 'Global search' }), term)
  return user
}

describe('GlobalSearch', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(searchApi.search).mockResolvedValue(results)
  })

  /** The WP's own shape: one box, results grouped by kind, every kind under its own heading. */
  it('groups the results by kind under the names the navigation uses', async () => {
    await type('core')

    await screen.findByText('Core switch is unreachable')
    expect(screen.getByText('Tickets')).toBeInTheDocument()
    expect(screen.getByText('Assets')).toBeInTheDocument()
    expect(screen.getByText('Alerts')).toBeInTheDocument()
    expect(screen.getByText('People')).toBeInTheDocument()
    expect(screen.getByText('DC1 core switch A')).toBeInTheDocument()
    // By the username rather than the display name: the same person is also the requester printed under
    // their ticket, which is the point of the WP's "requester name finds tickets and the user" step.
    expect(screen.getByRole('option', { name: /mhalstead/ })).toBeInTheDocument()
  })

  /** A searched kind that found nothing gets no heading — an empty group is a heading with no answer under it. */
  it('draws no heading for a kind that found nothing', async () => {
    await type('core')

    await screen.findByText('Core switch is unreachable')
    expect(screen.queryByText('Devices')).not.toBeInTheDocument()
  })

  /**
   * Below two characters nothing is sent at all: a one-letter prefix matches most of the estate, so it
   * would cost five wide reads to return a list nobody wants.
   */
  it('sends nothing until the term is worth searching for', async () => {
    await type('c')

    await waitFor(() => expect(searchApi.search).not.toHaveBeenCalled())
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  /**
   * The keyboard walks one flat list across the headings, so Down at the bottom of Tickets reaches the
   * first asset rather than stopping.
   */
  it('moves the highlight through every result across the group headings', async () => {
    const user = await type('core')
    await screen.findByText('Core switch is unreachable')

    await user.keyboard('{ArrowDown}')
    expect(screen.getByRole('option', { name: /Core switch is unreachable/ })).toHaveAttribute('aria-selected', 'true')

    await user.keyboard('{ArrowDown}{ArrowDown}')
    expect(screen.getByRole('option', { name: /DC1 core switch A/ })).toHaveAttribute('aria-selected', 'true')
  })

  /** Wrapping rather than stopping: Up from nothing selected lands on the last result. */
  it('wraps to the last result when Up is pressed first', async () => {
    const user = await type('core')
    await screen.findByText('Core switch is unreachable')

    await user.keyboard('{ArrowUp}')
    expect(screen.getByRole('option', { name: /mhalstead/ })).toHaveAttribute('aria-selected', 'true')
  })

  it('opens the highlighted result on Enter', async () => {
    const user = await type('core')
    await screen.findByText('Core switch is unreachable')

    await user.keyboard('{ArrowDown}{Enter}')

    expect(await screen.findByText('Ticket page')).toBeInTheDocument()
  })

  /**
   * Enter with nothing highlighted deliberately does nothing. Opening the first result for somebody who has
   * not chosen it is how a reader lands on a record they never looked at.
   */
  it('does nothing on Enter while nothing is highlighted', async () => {
    const user = await type('core')
    await screen.findByText('Core switch is unreachable')

    await user.keyboard('{Enter}')

    expect(screen.getByText('Overview')).toBeInTheDocument()
    expect(screen.queryByText('Ticket page')).not.toBeInTheDocument()
  })

  it('opens a clicked result', async () => {
    const user = await type('core')
    await screen.findByText('DC1 core switch A')

    await user.click(screen.getByRole('option', { name: /DC1 core switch A/ }))

    expect(await screen.findByText('Asset page')).toBeInTheDocument()
  })

  it('closes on Escape without navigating anywhere', async () => {
    const user = await type('core')
    await screen.findByText('Core switch is unreachable')

    await user.keyboard('{Escape}')

    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(screen.getByText('Overview')).toBeInTheDocument()
  })

  /**
   * WP-2.4's rule at the bottom of the dropdown: a capped list says how much it is not showing, per kind,
   * so five of forty-one reads as forty-one rather than as everything there is.
   */
  it('states the real totals behind a capped group', async () => {
    await type('core')

    await screen.findByText('Core switch is unreachable')
    expect(screen.getByText(/showing 2 of 41/)).toBeInTheDocument()
    expect(screen.getByText(/41 tickets, 5 alerts in all/)).toBeInTheDocument()
  })

  /**
   * WP-5.4's gibberish step. Not a bare "No data" (DESIGN §6): it names the term back so a typo is visible,
   * and it says what was searched — the two things somebody staring at an empty dropdown is deciding between.
   */
  it('says what was searched when nothing matched, rather than showing a bare empty list', async () => {
    vi.mocked(searchApi.search).mockResolvedValue(empty)
    await type('qwlkjhasdf')

    expect(await screen.findByText(/Nothing matches/)).toBeInTheDocument()
    expect(screen.getByText(/Tickets, assets, devices, alerts and people were all searched/)).toBeInTheDocument()
  })

  /**
   * A failed read is a fact about the request; nothing found is a claim about the estate. The two must not
   * read the same — the WP-2.11 rule.
   */
  it('reads differently when the search itself failed', async () => {
    vi.mocked(searchApi.search).mockRejectedValue(new Error('nope'))
    await type('core')

    expect(await screen.findByRole('alert')).toHaveTextContent('The search could not be run.')
    expect(screen.queryByText(/Nothing matches/)).not.toBeInTheDocument()
  })
})
