import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { assetsApi, type CiFilter, type CiPage } from '../../api/assets'
import { CiStatsRow } from './CiStatsRow'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, listCis: vi.fn() } }
})

const page: CiPage = { items: [], total: 7, page: 1, pageSize: 1 }

function renderRow(filter: CiFilter = {}, onSelect = vi.fn()) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const view = render(
    <QueryClientProvider client={client}><CiStatsRow filter={filter} onSelect={onSelect} /></QueryClientProvider>)
  return { ...view, onSelect }
}

beforeEach(() => {
  localStorage.clear()
  vi.clearAllMocks()
  vi.mocked(assetsApi.listCis).mockResolvedValue(page)
})

describe('the built-in tiles', () => {
  it('counts each one and applies its filter when clicked', async () => {
    const user = userEvent.setup()
    const { onSelect } = renderRow()

    // Located by its label then walked up: a tile's accessible name also matches its Remove button.
    const deployed = (await screen.findByText('Deployed')).closest('button')!
    expect(await within(deployed).findByText('7')).toBeInTheDocument()

    await user.click(deployed)

    expect(onSelect).toHaveBeenCalledWith({ lifecycleState: 'Deployed' })
  })

  /** A tile is never a filter the reader cannot undo. */
  it('clears the filter when an applied tile is clicked again', async () => {
    const user = userEvent.setup()
    const { onSelect } = renderRow({ lifecycleState: 'Deployed' })

    await user.click((await screen.findByText('Deployed')).closest('button')!)

    expect(onSelect).toHaveBeenCalledWith({})
  })

  /** A zero here would be read as a fact about the estate rather than as a failure. */
  it('says a count is unavailable rather than showing a zero', async () => {
    vi.mocked(assetsApi.listCis).mockRejectedValue(new Error('down'))
    renderRow()

    expect(await screen.findAllByText('Unavailable')).not.toHaveLength(0)
  })
})

describe('pinning a tile', () => {
  it('is not offered when the list is not narrowed', async () => {
    renderRow({})
    await screen.findByText('Configuration items')

    expect(screen.queryByRole('button', { name: /Pin as tile/ })).not.toBeInTheDocument()
  })

  /** Nothing is gained by pinning a slice that is already a tile. */
  it('is not offered when the current filter is already a tile', async () => {
    renderRow({ lifecycleState: 'Deployed' })
    await screen.findByText('Deployed')

    expect(screen.queryByRole('button', { name: /Pin as tile/ })).not.toBeInTheDocument()
  })

  it('saves the current filter under a name and counts it', async () => {
    const user = userEvent.setup()
    renderRow({ type: 'Hardware', lifecycleState: 'Retired' })
    await screen.findByText('Configuration items')

    await user.click(screen.getByRole('button', { name: /Pin as tile/ }))
    await user.clear(screen.getByLabelText('Name'))
    await user.type(screen.getByLabelText('Name'), 'Retired hardware')
    await user.click(screen.getByRole('button', { name: 'Save tile' }))

    const tile = (await screen.findByText('Retired hardware')).closest('button')!
    expect(await within(tile).findByText('7')).toBeInTheDocument()
    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(
      expect.objectContaining({ type: 'Hardware', lifecycleState: 'Retired' })))
  })

  /** The name defaults to what the tile counts, so it is never blank. */
  it('suggests a name describing the filter', async () => {
    const user = userEvent.setup()
    renderRow({ type: 'Hardware', lifecycleState: 'Retired' })
    await screen.findByText('Configuration items')

    await user.click(screen.getByRole('button', { name: /Pin as tile/ }))

    expect(screen.getByLabelText('Name')).toHaveValue('Hardware · Retired')
  })

  it('survives a remount and can be removed', async () => {
    const user = userEvent.setup()
    const first = renderRow({ type: 'Hardware' })
    await screen.findByText('Configuration items')
    await user.click(screen.getByRole('button', { name: /Pin as tile/ }))
    await user.click(screen.getByRole('button', { name: 'Save tile' }))
    await screen.findByRole('button', { name: 'Remove Hardware' })
    first.unmount()

    renderRow({ type: 'Hardware' })
    const pinned = await screen.findByRole('button', { name: 'Remove Hardware' })

    await user.click(pinned)

    expect(screen.queryByRole('button', { name: 'Remove Hardware' })).not.toBeInTheDocument()
  })

  /** Every tile is a request, so the number of them is capped rather than left to grow. */
  it('stops offering to pin once the limit is reached', async () => {
    localStorage.setItem('assets:tiles', JSON.stringify(
      [1, 2, 3, 4].map((index) => ({ id: `t${index}`, label: `Pinned ${index}`, filter: { contractId: `c${index}` } }))))

    renderRow({ type: 'Hardware' })
    await screen.findByText('Pinned 4')

    expect(screen.queryByRole('button', { name: /Pin as tile/ })).not.toBeInTheDocument()
    expect(screen.getByText(/8 tiles is the limit/)).toBeInTheDocument()
  })
})

describe('arranging the tiles', () => {
  /** A default is a suggestion, not a fixture — the row is the reader's to compose. */
  it('removes a built-in tile and offers it back from the menu', async () => {
    const user = userEvent.setup()
    renderRow()
    await screen.findByText('Deployed')

    await user.click(screen.getByRole('button', { name: 'Remove Deployed' }))

    expect(screen.queryByText('Deployed')).not.toBeInTheDocument()

    // Hidden, not destroyed: it is still listed in the menu, unticked.
    await user.click(screen.getByRole('button', { name: 'Tiles' }))
    const entry = screen.getByRole('checkbox', { name: 'Deployed' })
    expect(entry).not.toBeChecked()

    await user.click(entry)
    expect(screen.getAllByText('Deployed').length).toBeGreaterThan(0)
  })

  it('lets every tile be removed, leaving a row that can still be added to', async () => {
    const user = userEvent.setup()
    renderRow({ type: 'Hardware' })
    await screen.findByText('Deployed')

    for (const label of ['Configuration items', 'Deployed', 'In repair', 'Warranty ends within 30 days']) {
      await user.click(screen.getByRole('button', { name: `Remove ${label}` }))
    }

    expect(screen.queryByRole('group', { name: 'Estate counts' })).not.toBeInTheDocument()
    // The way back does not live inside the row it would have been removed with.
    expect(screen.getByRole('button', { name: 'Tiles' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: /Pin as tile/ })).toBeInTheDocument()
  })

  /** Only a pinned tile is editable; a built-in's filter is fixed. */
  it('renames a pinned tile and leaves built-ins alone', async () => {
    localStorage.setItem('assets:tiles', JSON.stringify(
      [{ id: 't1', label: 'Retired kit', filter: { lifecycleState: 'Retired' } }]))
    const user = userEvent.setup()
    renderRow()
    await screen.findByText('Retired kit')

    expect(screen.queryByRole('button', { name: 'Edit Deployed' })).not.toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Edit Retired kit' }))
    await user.clear(screen.getByLabelText('Name'))
    await user.type(screen.getByLabelText('Name'), 'Awaiting disposal')
    await user.click(screen.getByRole('button', { name: 'Save tile' }))

    expect(await screen.findByText('Awaiting disposal')).toBeInTheDocument()
    expect(screen.queryByText('Retired kit')).not.toBeInTheDocument()
  })

  /** A tile that has been outgrown can be re-pointed without deleting and pinning again. */
  it('can be re-pointed at what the list shows now', async () => {
    localStorage.setItem('assets:tiles', JSON.stringify(
      [{ id: 't1', label: 'My slice', filter: { lifecycleState: 'Retired' } }]))
    const user = userEvent.setup()
    renderRow({ type: 'Hardware', lifecycleState: 'Deployed' })
    await screen.findByText('My slice')

    await user.click(screen.getByRole('button', { name: 'Edit My slice' }))
    await user.click(screen.getByRole('checkbox', { name: /Point it at what the list shows now/ }))
    await user.click(screen.getByRole('button', { name: 'Save tile' }))

    await waitFor(() => expect(assetsApi.listCis).toHaveBeenCalledWith(
      expect.objectContaining({ type: 'Hardware', lifecycleState: 'Deployed', pageSize: 1 })))
  })

  /** Re-pointing is only offered when it would change something. */
  it('does not offer to re-point when the tile already matches the list', async () => {
    localStorage.setItem('assets:tiles', JSON.stringify(
      [{ id: 't1', label: 'My slice', filter: { lifecycleState: 'Retired' } }]))
    const user = userEvent.setup()
    renderRow({ lifecycleState: 'Retired' })
    await screen.findByText('My slice')

    await user.click(screen.getByRole('button', { name: 'Edit My slice' }))

    expect(screen.queryByRole('checkbox', { name: /Point it at what the list shows now/ })).not.toBeInTheDocument()
  })

  it('reorders when a tile is dropped on another, and remembers it', async () => {
    const first = renderRow()
    await screen.findByText('Deployed')

    const order = () => screen.getAllByRole('button', { name: /^Remove / })
      .map((button) => button.getAttribute('aria-label'))
    expect(order()[0]).toBe('Remove Configuration items')

    const source = screen.getByText('In repair').closest('[draggable]')!
    const target = screen.getByText('Configuration items').closest('[draggable]')!
    fireEvent.dragStart(source)
    fireEvent.dragOver(target)
    fireEvent.drop(target)

    expect(order()[0]).toBe('Remove In repair')
    first.unmount()

    renderRow()
    await screen.findByText('In repair')
    expect(order()[0]).toBe('Remove In repair')
  })
})

/**
 * A tile's own count query is its stored filter. Storing the raw filter would carry the search term
 * into it, and the tile would quietly count a different thing from what its name says.
 */
test('a pinned tile stores only what a tile keeps, not the search term or the page', async () => {
  const user = userEvent.setup()
  renderRow({ type: 'Hardware', search: 'switch', page: 4, pageSize: 25 })
  await screen.findByText('Configuration items')

  await user.click(screen.getByRole('button', { name: /Pin as tile/ }))
  await user.click(screen.getByRole('button', { name: 'Save tile' }))
  await screen.findByRole('button', { name: /^Remove Hardware$/ })

  const stored = JSON.parse(localStorage.getItem('assets:tiles') ?? '[]')
  expect(stored[0].filter).toEqual({ type: 'Hardware' })
})

describe('choosing an icon', () => {
  it('pins a tile with the icon that was chosen, and keeps it on the tile', async () => {
    const user = userEvent.setup()
    renderRow({ type: 'Hardware' })
    await screen.findByText('Configuration items')

    await user.click(screen.getByRole('button', { name: /Pin as tile/ }))
    await user.click(screen.getByRole('radio', { name: 'Laptop' }))
    await user.click(screen.getByRole('button', { name: 'Save tile' }))

    await screen.findByRole('button', { name: /^Remove Hardware$/ })
    expect(JSON.parse(localStorage.getItem('assets:tiles') ?? '[]')[0].icon).toBe('laptop')
  })

  it('defaults to the pin when nothing is chosen', async () => {
    const user = userEvent.setup()
    renderRow({ type: 'Hardware' })
    await screen.findByText('Configuration items')

    await user.click(screen.getByRole('button', { name: /Pin as tile/ }))
    expect(screen.getByRole('radio', { name: 'Pin' })).toBeChecked()

    await user.click(screen.getByRole('button', { name: 'Save tile' }))

    await screen.findByRole('button', { name: /^Remove Hardware$/ })
    expect(JSON.parse(localStorage.getItem('assets:tiles') ?? '[]')[0].icon).toBe('pin')
  })

  /** Editing opens on the icon the tile already carries rather than resetting it. */
  it('opens the editor on the tile\'s current icon and can change it', async () => {
    localStorage.setItem('assets:tiles', JSON.stringify(
      [{ id: 't1', label: 'Printers', icon: 'printer', filter: { type: 'Hardware' } }]))
    const user = userEvent.setup()
    renderRow()
    await screen.findByText('Printers')

    await user.click(screen.getByRole('button', { name: 'Edit Printers' }))
    expect(screen.getByRole('radio', { name: 'Printer' })).toBeChecked()

    await user.click(screen.getByRole('radio', { name: 'Archive' }))
    await user.click(screen.getByRole('button', { name: 'Save tile' }))

    await waitFor(() => expect(JSON.parse(localStorage.getItem('assets:tiles') ?? '[]')[0].icon).toBe('archive'))
  })

  /** A tile stored before icons existed, or naming a retired one, still draws. */
  it('draws a tile whose icon is missing or unknown', async () => {
    localStorage.setItem('assets:tiles', JSON.stringify([
      { id: 't1', label: 'Older tile', filter: { type: 'Hardware' } },
      { id: 't2', label: 'Retired icon', icon: 'no-such-icon', filter: { type: 'Server' } },
    ]))
    renderRow()

    expect(await screen.findByText('Older tile')).toBeInTheDocument()
    expect(screen.getByText('Retired icon')).toBeInTheDocument()
  })
})

describe('choosing a colour', () => {
  it('pins a tile with the colour that was chosen', async () => {
    const user = userEvent.setup()
    renderRow({ type: 'Hardware' })
    await screen.findByText('Configuration items')

    await user.click(screen.getByRole('button', { name: /Pin as tile/ }))
    await user.click(screen.getByRole('radio', { name: 'Red' }))
    await user.click(screen.getByRole('button', { name: 'Save tile' }))

    await screen.findByRole('button', { name: /^Remove Hardware$/ })
    expect(JSON.parse(localStorage.getItem('assets:tiles') ?? '[]')[0].tone).toBe('red')
  })

  it('defaults to neutral when no colour is chosen', async () => {
    const user = userEvent.setup()
    renderRow({ type: 'Hardware' })
    await screen.findByText('Configuration items')

    await user.click(screen.getByRole('button', { name: /Pin as tile/ }))
    expect(screen.getByRole('radio', { name: 'Neutral' })).toBeChecked()

    await user.click(screen.getByRole('button', { name: 'Save tile' }))

    await screen.findByRole('button', { name: /^Remove Hardware$/ })
    expect(JSON.parse(localStorage.getItem('assets:tiles') ?? '[]')[0].tone).toBe('slate')
  })

  it('opens the editor on the colour the tile already carries and can change it', async () => {
    localStorage.setItem('assets:tiles', JSON.stringify(
      [{ id: 't1', label: 'Overdue', icon: 'clock', tone: 'amber', filter: { type: 'Hardware' } }]))
    const user = userEvent.setup()
    renderRow()
    await screen.findByText('Overdue')

    await user.click(screen.getByRole('button', { name: 'Edit Overdue' }))
    expect(screen.getByRole('radio', { name: 'Amber' })).toBeChecked()

    await user.click(screen.getByRole('radio', { name: 'Teal' }))
    await user.click(screen.getByRole('button', { name: 'Save tile' }))

    await waitFor(() => expect(JSON.parse(localStorage.getItem('assets:tiles') ?? '[]')[0].tone).toBe('teal'))
  })

  /** Icon and colour are separate choices; changing one must not reset the other. */
  it('keeps the icon when only the colour is changed', async () => {
    localStorage.setItem('assets:tiles', JSON.stringify(
      [{ id: 't1', label: 'Overdue', icon: 'clock', tone: 'amber', filter: { type: 'Hardware' } }]))
    const user = userEvent.setup()
    renderRow()
    await screen.findByText('Overdue')

    await user.click(screen.getByRole('button', { name: 'Edit Overdue' }))
    await user.click(screen.getByRole('radio', { name: 'Red' }))
    await user.click(screen.getByRole('button', { name: 'Save tile' }))

    await waitFor(() => {
      const stored = JSON.parse(localStorage.getItem('assets:tiles') ?? '[]')[0]
      expect(stored).toMatchObject({ icon: 'clock', tone: 'red' })
    })
  })

  /** A tile naming a colour this version no longer offers still draws. */
  it('draws a tile whose colour is missing or unknown', async () => {
    localStorage.setItem('assets:tiles', JSON.stringify([
      { id: 't1', label: 'No colour', filter: { type: 'Hardware' } },
      { id: 't2', label: 'Retired colour', tone: 'chartreuse', filter: { type: 'Server' } },
    ]))
    renderRow()

    expect(await screen.findByText('No colour')).toBeInTheDocument()
    expect(screen.getByText('Retired colour')).toBeInTheDocument()
  })
})
