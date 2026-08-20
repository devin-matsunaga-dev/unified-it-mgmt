import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { vi } from 'vitest'
import { assetsApi, type IdentifyDeviceResponse } from '../../api/assets'
import { ScanDeviceDialog, toSeed } from './ScanDeviceDialog'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, identifyDevice: vi.fn() } }
})

const unknown: IdentifyDeviceResponse = {
  identifiers: [{ scanned: 'ABC123', value: 'ABC123', kind: 'Unknown' }],
  rejected: [],
  result: {
    manufacturer: null, model: null, productNumber: null, serialNumber: null,
    deviceType: null, source: 'Unknown', confidence: 'Unknown',
  },
}

const recognised: IdentifyDeviceResponse = {
  identifiers: [
    { scanned: 'P/N: 12RQ000KUS', value: '12RQ000KUS', kind: 'ModelIdentifier' },
    { scanned: 'S/N: MZ00H8S2', value: 'MZ00H8S2', kind: 'SerialNumber' },
  ],
  rejected: [],
  result: {
    manufacturer: 'Lenovo', model: 'ThinkPad L14 Gen 2', productNumber: '12RQ000KUS',
    serialNumber: 'MZ00H8S2', deviceType: 'Laptop', source: 'Manual', confidence: 'High',
  },
}

function renderDialog(onConfirm = vi.fn()) {
  const client = new QueryClient({ defaultOptions: { mutations: { retry: false } } })
  render(<QueryClientProvider client={client}>
    <ScanDeviceDialog open onClose={vi.fn()} onConfirm={onConfirm} />
  </QueryClientProvider>)
  return onConfirm
}

describe('toSeed', () => {
  it('carries the make and model in as attributes, where a Hardware CI keeps them', () => {
    expect(toSeed(recognised)).toEqual({
      name: 'ThinkPad L14 Gen 2',
      serialNumber: 'MZ00H8S2',
      attributes: { manufacturer: 'Lenovo', model: 'ThinkPad L14 Gen 2' },
    })
  })

  /** Nothing identified still hands over the serial — that is the one fact the scans did establish. */
  it('carries a serial through even when nothing was identified', () => {
    expect(toSeed({
      ...unknown,
      identifiers: [{ scanned: 'S/N: ABC123', value: 'ABC123', kind: 'SerialNumber' }],
    })).toEqual({ name: undefined, serialNumber: 'ABC123', attributes: undefined })
  })

  it('is empty when there is nothing to carry', () => {
    expect(toSeed(null)).toEqual({})
  })
})

describe('ScanDeviceDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(unknown)
  })

  it('identifies the whole set each time a code is added', async () => {
    renderDialog()

    await userEvent.type(screen.getByLabelText('Scan or type a code'), 'P/N: 12RQ000KUS{Enter}')
    await waitFor(() => expect(assetsApi.identifyDevice).toHaveBeenLastCalledWith(['P/N: 12RQ000KUS']))

    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)
    await userEvent.type(screen.getByLabelText('Scan or type a code'), 'S/N: MZ00H8S2{Enter}')

    await waitFor(() => expect(assetsApi.identifyDevice)
      .toHaveBeenLastCalledWith(['P/N: 12RQ000KUS', 'S/N: MZ00H8S2']))
  })

  it('shows what was detected, with its source and confidence', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)

    renderDialog()
    await userEvent.type(screen.getByLabelText('Scan or type a code'), 'P/N: 12RQ000KUS{Enter}')

    expect(await screen.findByText('Lenovo ThinkPad L14 Gen 2')).toBeInTheDocument()
    expect(screen.getByText('High')).toBeInTheDocument()
    expect(screen.getByText(/Identification source: Manual/)).toBeInTheDocument()
  })

  it('lists each identifier with the kind the server gave it', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)

    renderDialog()
    await userEvent.type(screen.getByLabelText('Scan or type a code'), 'P/N: 12RQ000KUS{Enter}')

    const list = await screen.findByRole('list', { name: 'Scanned identifiers' })
    expect(within(list).getByText('Model / product')).toBeInTheDocument()
    expect(within(list).getByText('Serial number')).toBeInTheDocument()
  })

  it('flags a result that is not an exact match', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue({
      ...recognised, result: { ...recognised.result, confidence: 'Low' },
    })

    renderDialog()
    await userEvent.type(screen.getByLabelText('Scan or type a code'), 'P/N: 12RQ000KUS{Enter}')

    expect(await screen.findByText(/Not an exact match/)).toBeInTheDocument()
  })

  it('will not confirm before anything has been scanned', () => {
    renderDialog()

    expect(screen.getByRole('button', { name: /Confirm/ })).toBeDisabled()
  })

  /** Confirming opens a form. It must never be the thing that creates an asset. */
  it('hands the identification out rather than creating anything', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)
    const onConfirm = renderDialog()

    await userEvent.type(screen.getByLabelText('Scan or type a code'), 'P/N: 12RQ000KUS{Enter}')
    await screen.findByText('Lenovo ThinkPad L14 Gen 2')
    await userEvent.click(screen.getByRole('button', { name: /Confirm/ }))

    expect(onConfirm).toHaveBeenCalledWith({
      name: 'ThinkPad L14 Gen 2',
      serialNumber: 'MZ00H8S2',
      attributes: { manufacturer: 'Lenovo', model: 'ThinkPad L14 Gen 2' },
    })
  })

  it('lets an unidentified device through to be filled in by hand', async () => {
    const onConfirm = renderDialog()

    await userEvent.type(screen.getByLabelText('Scan or type a code'), 'ABC123{Enter}')
    expect(await screen.findByText(/could not be identified/)).toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: /Confirm/ }))
    expect(onConfirm).toHaveBeenCalled()
  })

  it('re-identifies without a code that was removed', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)

    renderDialog()
    await userEvent.type(screen.getByLabelText('Scan or type a code'), 'P/N: 12RQ000KUS{Enter}')
    await userEvent.click(await screen.findByRole('button', { name: 'Remove MZ00H8S2' }))

    await waitFor(() => expect(assetsApi.identifyDevice).toHaveBeenLastCalledWith(['P/N: 12RQ000KUS']))
  })
})
