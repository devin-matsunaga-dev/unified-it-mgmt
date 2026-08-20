import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci, type CiTypeSchema, type IdentifyDeviceResponse } from '../../api/assets'
import { FieldReceivePage } from './FieldReceivePage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return {
    ...actual,
    assetsApi: {
      ...actual.assetsApi,
      identifyDevice: vi.fn(),
      createCi: vi.fn(),
      listTypeSchemas: vi.fn(),
      saveProductCatalogEntry: vi.fn(),
    },
  }
})
vi.mock('./useQrCamera', () => ({
  useQrCamera: () => ({ videoRef: { current: null }, status: 'idle', start: vi.fn(), stop: vi.fn() }),
}))

/** Manufacturer and model are required for Hardware, exactly as CiTypeSchema declares them. */
const schemas: CiTypeSchema[] = [{
  type: 'Hardware',
  attributes: [
    { key: 'manufacturer', label: 'Manufacturer', kind: 'Text', isRequired: true, allowedValues: [] },
    { key: 'model', label: 'Model', kind: 'Text', isRequired: true, allowedValues: [] },
  ],
  customFields: [],
}]

const identified = (over: Partial<IdentifyDeviceResponse> = {}): IdentifyDeviceResponse => ({
  identifiers: [], rejected: [],
  result: {
    manufacturer: null, model: null, productNumber: null, serialNumber: null,
    deviceType: null, source: 'Unknown', confidence: 'Unknown',
  },
  ...over,
})

const recognised: IdentifyDeviceResponse = {
  identifiers: [
    { scanned: '1S12RQ000KUSMZ00H8S2', value: '12RQ000KUS', kind: 'ModelIdentifier' },
    { scanned: '1S12RQ000KUSMZ00H8S2', value: 'MZ00H8S2', kind: 'SerialNumber' },
  ],
  rejected: [],
  result: {
    manufacturer: 'Lenovo', model: 'ThinkPad L14 Gen 2', productNumber: '12RQ000KUS',
    serialNumber: 'MZ00H8S2', deviceType: 'Laptop', source: 'Manual', confidence: 'High',
  },
}

function renderPage(entry = '/field/receive') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter initialEntries={[entry]}>
    <QueryClientProvider client={client}>
      <Routes>
        <Route path="/field/receive" element={<FieldReceivePage />} />
        <Route path="/field/ci/:id" element={<h1>Field asset page</h1>} />
      </Routes>
    </QueryClientProvider>
  </MemoryRouter>)
}

describe('FieldReceivePage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(assetsApi.listTypeSchemas).mockResolvedValue(schemas)
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified())
  })

  it('identifies a code carried in from the scan screen without a second scan', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)

    renderPage('/field/receive?code=1S12RQ000KUSMZ00H8S2')

    await waitFor(() => expect(assetsApi.identifyDevice).toHaveBeenCalledWith(['1S12RQ000KUSMZ00H8S2']))
    expect(await screen.findByText('Lenovo ThinkPad L14 Gen 2')).toBeInTheDocument()
  })

  /** More scans, better answer: the whole set is re-identified rather than each code in isolation. */
  it('re-identifies the whole set each time a scan is added', async () => {
    renderPage()

    await userEvent.type(screen.getByLabelText('Type a code'), 'P/N: 12RQ000KUS')
    await userEvent.click(screen.getByRole('button', { name: 'Add' }))
    await waitFor(() => expect(assetsApi.identifyDevice).toHaveBeenLastCalledWith(['P/N: 12RQ000KUS']))

    await userEvent.type(screen.getByLabelText('Type a code'), 'S/N: MZ00H8S2')
    await userEvent.click(screen.getByRole('button', { name: 'Add' }))

    await waitFor(() => expect(assetsApi.identifyDevice)
      .toHaveBeenLastCalledWith(['P/N: 12RQ000KUS', 'S/N: MZ00H8S2']))
  })

  it('lists each identifier with what the server decided it was', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)

    renderPage('/field/receive?code=1S12RQ000KUSMZ00H8S2')

    const list = await screen.findByRole('list', { name: 'Scanned identifiers' })
    // Scoped to the list: "Serial number" is also a form label further down the page.
    expect(within(list).getByText('12RQ000KUS')).toBeInTheDocument()
    expect(within(list).getByText('Model / product')).toBeInTheDocument()
    expect(within(list).getByText('MZ00H8S2')).toBeInTheDocument()
    expect(within(list).getByText('Serial number')).toBeInTheDocument()
  })

  it('fills the form from a confident answer, and says where it came from', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)

    renderPage('/field/receive?code=1S12RQ000KUSMZ00H8S2')

    expect(await screen.findByLabelText('Manufacturer')).toHaveValue('Lenovo')
    expect(screen.getByLabelText('Model')).toHaveValue('ThinkPad L14 Gen 2')
    expect(screen.getByLabelText('Serial number')).toHaveValue('MZ00H8S2')
    expect(screen.getByText('High')).toBeInTheDocument()
    expect(screen.getByText(/Source: Manual/)).toBeInTheDocument()
  })

  /** A partial match is shown and flagged, never quietly treated as settled. */
  it('warns that a result below an exact match needs checking', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue({
      ...recognised,
      result: { ...recognised.result, confidence: 'Low' },
    })

    renderPage('/field/receive?code=ABC')

    expect(await screen.findByText(/not an exact match/)).toBeInTheDocument()
  })

  it('offers the manual path when nothing could be identified', async () => {
    renderPage('/field/receive?code=ABC123')

    expect(await screen.findByText(/could not be identified/)).toBeInTheDocument()
    expect(screen.getByLabelText('Manufacturer')).toHaveValue('')
  })

  it('tells the technician when a scan was refused outright', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified({ rejected: ['AAAA…'] }))

    renderPage('/field/receive?code=' + 'A'.repeat(140))

    await waitFor(() => expect(assetsApi.identifyDevice).toHaveBeenCalled())
    expect(screen.getByText('Nothing yet.')).toBeInTheDocument()
  })

  it('does not overwrite something the technician has already typed', async () => {
    renderPage()

    // The attribute fields come from the type schema, so wait for it before typing into one.
    await userEvent.type(await screen.findByLabelText('Manufacturer'), 'Dell')
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)
    await userEvent.type(screen.getByLabelText('Type a code'), '1S12RQ000KUSMZ00H8S2')
    await userEvent.click(screen.getByRole('button', { name: 'Add' }))

    await waitFor(() => expect(screen.getByLabelText('Model')).toHaveValue('ThinkPad L14 Gen 2'))
    expect(screen.getByLabelText('Manufacturer')).toHaveValue('Dell')
  })

  it('will not register until every required attribute carries something', async () => {
    renderPage('/field/receive?code=ABC123')

    await screen.findByLabelText('Manufacturer')
    expect(screen.getByRole('button', { name: /Register it/ })).toBeDisabled()

    await userEvent.type(screen.getByLabelText('What is it?'), 'Latitude 5450')
    await userEvent.type(screen.getByLabelText('Manufacturer'), 'Dell')
    expect(screen.getByRole('button', { name: /Register it/ })).toBeDisabled()

    await userEvent.type(screen.getByLabelText('Model'), 'Latitude 5450')
    expect(screen.getByRole('button', { name: /Register it/ })).toBeEnabled()
  })

  it('registers the device and remembers the model for the next one', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)
    vi.mocked(assetsApi.createCi).mockResolvedValue({ id: 'ci-new', name: 'ThinkPad L14 Gen 2' } as Ci)
    vi.mocked(assetsApi.saveProductCatalogEntry).mockResolvedValue({} as never)

    renderPage('/field/receive?code=1S12RQ000KUSMZ00H8S2')
    await userEvent.type(await screen.findByLabelText('What is it?'), 'Reception laptop')
    await userEvent.click(screen.getByRole('button', { name: /Register it/ }))

    await waitFor(() => expect(assetsApi.createCi).toHaveBeenCalledWith(expect.objectContaining({
      type: 'Hardware',
      serialNumber: 'MZ00H8S2',
      attributes: { manufacturer: 'Lenovo', model: 'ThinkPad L14 Gen 2' },
    })))
    // Keyed on the product identifier, never on the serial.
    await waitFor(() => expect(assetsApi.saveProductCatalogEntry).toHaveBeenCalledWith({
      modelIdentifier: '12RQ000KUS',
      manufacturer: 'Lenovo',
      model: 'ThinkPad L14 Gen 2',
    }))
  })

  it('does not remember the model when the technician unticks it', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)
    vi.mocked(assetsApi.createCi).mockResolvedValue({ id: 'ci-new', name: 'x' } as Ci)

    renderPage('/field/receive?code=1S12RQ000KUSMZ00H8S2')
    await userEvent.type(await screen.findByLabelText('What is it?'), 'Reception laptop')
    await userEvent.click(screen.getByRole('checkbox', { name: /Remember this model/ }))
    await userEvent.click(screen.getByRole('button', { name: /Register it/ }))

    await waitFor(() => expect(assetsApi.createCi).toHaveBeenCalled())
    expect(assetsApi.saveProductCatalogEntry).not.toHaveBeenCalled()
  })

  /** The mapping is a convenience; the device is the thing that had to be recorded. */
  it('still registers the device when remembering the model fails', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)
    vi.mocked(assetsApi.createCi).mockResolvedValue({ id: 'ci-new', name: 'x' } as Ci)
    vi.mocked(assetsApi.saveProductCatalogEntry).mockRejectedValue(new Error('Network request failed'))

    renderPage('/field/receive?code=1S12RQ000KUSMZ00H8S2')
    await userEvent.type(await screen.findByLabelText('What is it?'), 'Reception laptop')
    await userEvent.click(screen.getByRole('button', { name: /Register it/ }))

    expect(await screen.findByRole('heading', { name: 'Field asset page' })).toBeInTheDocument()
  })

  it('re-identifies without a scan the technician removed', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)

    renderPage('/field/receive?code=1S12RQ000KUSMZ00H8S2')
    await userEvent.click(await screen.findByRole('button', { name: 'Remove 12RQ000KUS' }))

    await waitFor(() => expect(screen.getByText('Nothing yet.')).toBeInTheDocument())
  })
})
