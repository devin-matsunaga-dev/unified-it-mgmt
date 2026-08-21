import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci, type CiTypeSchema, type IdentifyDeviceResponse } from '../../api/assets'
import { ApiError } from '../../api/client'
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
      lookupCi: vi.fn(),
    },
  }
})
let emitCode: (code: string) => void = () => {}
let scanRegion: unknown
let scanManual: boolean | undefined
/** What the shutter finds when pressed. Null stands for a press that read nothing. */
let captureResult: string | null = null
const cameraStatus = { value: 'idle' as string }
vi.mock('./useQrCamera', () => ({
  useQrCamera: (onCode: (code: string) => void, options?: { region?: unknown; manual?: boolean }) => {
    emitCode = onCode
    scanRegion = options?.region
    scanManual = options?.manual
    return {
      videoRef: { current: null },
      status: cameraStatus.value,
      start: vi.fn(() => { cameraStatus.value = 'scanning' }),
      stop: vi.fn(() => { cameraStatus.value = 'idle' }),
      capturing: false,
      capture: vi.fn(async () => {
        if (captureResult === null) return false
        onCode(captureResult)
        return true
      }),
    }
  },
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
    cameraStatus.value = 'idle'
    captureResult = null
    vi.mocked(assetsApi.listTypeSchemas).mockResolvedValue(schemas)
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified())
    // Not registered is the ordinary answer for a device being received.
    vi.mocked(assetsApi.lookupCi).mockRejectedValue(new ApiError(404, 'Not found'))
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
    // Scoped to the list: "Serial number" is also a form label further down the page. Both rows here
    // have been assigned, so each says the role it plays rather than the kind the parser gave it.
    expect(within(list).getByText('12RQ000KUS')).toBeInTheDocument()
    expect(within(list).getByText('Model code')).toBeInTheDocument()
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
    expect(screen.getByText('No code scanned yet.')).toBeInTheDocument()
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

    await userEvent.type(screen.getByLabelText('Device name'), 'Latitude 5450')
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
    await userEvent.type(await screen.findByLabelText('Device name'), 'Reception laptop')
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
    await userEvent.type(await screen.findByLabelText('Device name'), 'Reception laptop')
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
    await userEvent.type(await screen.findByLabelText('Device name'), 'Reception laptop')
    await userEvent.click(screen.getByRole('button', { name: /Register it/ }))

    expect(await screen.findByRole('heading', { name: 'Field asset page' })).toBeInTheDocument()
  })

  it('re-identifies without a scan the technician removed', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)

    renderPage('/field/receive?code=1S12RQ000KUSMZ00H8S2')
    await userEvent.click(await screen.findByRole('button', { name: 'Remove 12RQ000KUS' }))

    await waitFor(() => expect(screen.getByText('No code scanned yet.')).toBeInTheDocument())
  })

  /**
   * Device labels crowd the serial, the product code and a shipping reference within a couple of
   * centimetres. Decoding the whole frame returns whichever resolved first, which is what made the
   * aiming guide a lie — it now decodes only the guide's area.
   */
  it('decodes only the aiming window, not the whole frame', async () => {
    renderPage()

    await screen.findByLabelText('Device name')
    expect(scanRegion).toEqual({ widthRatio: 0.85, heightRatio: 0.28 })
  })

  /**
   * The technician aimed at the serial barcode and said so, which beats any pattern the parser could
   * apply — so the read is labelled a serial on the way to identification, not left unclassified.
   */
  it('puts a declared serial straight in the field and identifies it as one', async () => {
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'Scan serial number' }))
    act(() => emitCode('FDO12345678'))
    await userEvent.click(await screen.findByRole('button', { name: 'Use it' }))

    await waitFor(() => expect(screen.getByLabelText('Serial number')).toHaveValue('FDO12345678'))
    expect(assetsApi.identifyDevice).toHaveBeenLastCalledWith(['S/N: FDO12345678'])
  })

  it('still adds an undeclared scan to the identification set rather than the serial field', async () => {
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: /Scan the model code/ }))
    act(() => emitCode('WS-C2960X-24TS-L'))
    await userEvent.click(await screen.findByRole('button', { name: 'Use it' }))

    await waitFor(() => expect(assetsApi.identifyDevice).toHaveBeenLastCalledWith(['WS-C2960X-24TS-L']))
    expect(screen.getByLabelText('Serial number')).toHaveValue('')
  })

  /**
   * Reported from the field: a scanned serial left the field empty and had to be retyped. Most
   * manufacturers print a bare alphanumeric, which the parser will not classify — but refusing to
   * classify it is no reason to make somebody type what they just scanned.
   */
  it('fills the serial from the only unclassified scan when the server named none', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified({
      identifiers: [{ scanned: 'FDO12345678', value: 'FDO12345678', kind: 'Unknown' }],
    }))

    renderPage('/field/receive?code=FDO12345678')

    await waitFor(() => expect(screen.getByLabelText('Serial number')).toHaveValue('FDO12345678'))
  })

  /** With two unclassified codes, which one is the serial is a guess — so it stays a tap. */
  it('does not guess the serial when more than one scan could be it', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified({
      identifiers: [
        { scanned: 'FDO12345678', value: 'FDO12345678', kind: 'Unknown' },
        { scanned: 'WS-C2960X', value: 'WS-C2960X', kind: 'Unknown' },
      ],
    }))

    renderPage('/field/receive?code=FDO12345678')

    await screen.findByRole('list', { name: 'Scanned identifiers' })
    expect(screen.getByLabelText('Serial number')).toHaveValue('')
  })

  it('sets the serial from its own field rather than from a row', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified({
      identifiers: [
        { scanned: 'DNI152602HL', value: 'DNI152602HL', kind: 'Unknown' },
        { scanned: 'WS-C2960X', value: 'WS-C2960X', kind: 'Unknown' },
      ],
    }))

    renderPage('/field/receive?code=DNI152602HL')

    // Rows place the model code only; the serial has a dedicated field with its own scan button, and
    // two controls for one job is what this removed.
    const list = await screen.findByRole('list', { name: 'Scanned identifiers' })
    expect(within(list).queryByRole('button', { name: 'Serial' })).not.toBeInTheDocument()

    await userEvent.click(screen.getByRole('button', { name: 'Scan serial number' }))
    act(() => emitCode('DNI152602HL'))
    await userEvent.click(await screen.findByRole('button', { name: 'Use it' }))

    await waitFor(() => expect(screen.getByLabelText('Serial number')).toHaveValue('DNI152602HL'))
  })

  it('does not overwrite a serial the technician already typed', async () => {
    renderPage()

    await userEvent.type(await screen.findByLabelText('Serial number'), 'TYPED123')
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified({
      identifiers: [{ scanned: 'FDO12345678', value: 'FDO12345678', kind: 'Unknown' }],
    }))
    await userEvent.type(screen.getByLabelText('Type a code'), 'FDO12345678')
    await userEvent.click(screen.getByRole('button', { name: 'Add' }))

    await waitFor(() => expect(assetsApi.identifyDevice).toHaveBeenCalled())
    expect(screen.getByLabelText('Serial number')).toHaveValue('TYPED123')
  })

  /**
   * Reported from the field: two switches of one model, and the second did not identify. The
   * catalogue is keyed on a product code, both scans were bare serials, so nothing was written — with
   * the box ticked and nothing said. A ticked box that saves nothing is worse than an unticked one.
   */
  it('says so when there is nothing to remember the model by', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified({
      identifiers: [{ scanned: 'DNI152602HL', value: 'DNI152602HL', kind: 'Unknown' }],
    }))

    renderPage('/field/receive?code=DNI152602HL')

    expect(await screen.findByText(/No model code yet/)).toBeInTheDocument()
  })

  it('lets the technician name the model code, and then remembers it', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified({
      identifiers: [
        { scanned: 'DNI152602HL', value: 'DNI152602HL', kind: 'Unknown' },
        { scanned: 'WS-C2960X-24TS-L', value: 'WS-C2960X-24TS-L', kind: 'Unknown' },
      ],
    }))
    vi.mocked(assetsApi.createCi).mockResolvedValue({ id: 'ci-new', name: 'Switch' } as Ci)
    vi.mocked(assetsApi.saveProductCatalogEntry).mockResolvedValue({} as never)

    renderPage('/field/receive?code=DNI152602HL')

    const list = await screen.findByRole('list', { name: 'Scanned identifiers' })
    await userEvent.click(within(list).getAllByRole('button', { name: 'Model' })[1])
    expect(screen.queryByText(/No model code yet/)).not.toBeInTheDocument()

    await userEvent.type(screen.getByLabelText('Device name'), 'Access switch')
    await userEvent.type(screen.getByLabelText('Manufacturer'), 'Cisco')
    await userEvent.type(screen.getByLabelText('Model'), 'Catalyst 2960-X')
    await userEvent.click(screen.getByRole('button', { name: /Register it/ }))

    await waitFor(() => expect(assetsApi.saveProductCatalogEntry).toHaveBeenCalledWith({
      modelIdentifier: 'WS-C2960X-24TS-L',
      manufacturer: 'Cisco',
      model: 'Catalyst 2960-X',
    }))
  })

  /**
   * A regression this screen once had and lost in the rewrite: a device already in the CMDB let
   * somebody fill in a whole form before the server refused the duplicate serial.
   */
  it('warns when a scanned code already belongs to a registered asset', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified({
      identifiers: [{ scanned: 'DNI152602HL', value: 'DNI152602HL', kind: 'Unknown' }],
    }))
    vi.mocked(assetsApi.lookupCi).mockResolvedValue({ id: 'ci-old', name: 'Comms room switch' } as Ci)

    renderPage('/field/receive?code=DNI152602HL')

    expect(await screen.findByRole('alert')).toHaveTextContent('Already registered')
    expect(screen.getByText('Comms room switch')).toBeInTheDocument()
  })

  it('opens the existing asset rather than making a second record for it', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified({
      identifiers: [{ scanned: 'DNI152602HL', value: 'DNI152602HL', kind: 'Unknown' }],
    }))
    vi.mocked(assetsApi.lookupCi).mockResolvedValue({ id: 'ci-old', name: 'Comms room switch' } as Ci)

    renderPage('/field/receive?code=DNI152602HL')
    await userEvent.click(await screen.findByRole('button', { name: 'Open it instead' }))

    expect(await screen.findByRole('heading', { name: 'Field asset page' })).toBeInTheDocument()
  })

  it('does not warn about a device nobody has registered', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified({
      identifiers: [{ scanned: 'DNI152602HL', value: 'DNI152602HL', kind: 'Unknown' }],
    }))

    renderPage('/field/receive?code=DNI152602HL')

    await screen.findByRole('list', { name: 'Scanned identifiers' })
    expect(screen.queryByText('Already registered')).not.toBeInTheDocument()
  })

  /**
   * The flow was turned around at the human's request: a serial names one device and can never
   * identify the next, so the main button now aims at the model or product code and the serial has
   * its own field.
   */
  it('treats the main scan as the model code, so remembering works by default', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified({
      identifiers: [{ scanned: 'WS-C2960X-24TS-L', value: 'WS-C2960X-24TS-L', kind: 'Unknown' }],
    }))
    vi.mocked(assetsApi.createCi).mockResolvedValue({ id: 'ci-new', name: 'Switch' } as Ci)
    vi.mocked(assetsApi.saveProductCatalogEntry).mockResolvedValue({} as never)

    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: /Scan the model code/ }))
    act(() => emitCode('WS-C2960X-24TS-L'))
    await userEvent.click(await screen.findByRole('button', { name: 'Use it' }))

    // No warning: the primary scan named a model code, which is the point of turning it around.
    await waitFor(() => expect(screen.queryByText(/No model code yet/)).not.toBeInTheDocument())

    await userEvent.type(screen.getByLabelText('Device name'), 'Access switch')
    await userEvent.type(screen.getByLabelText('Manufacturer'), 'Cisco')
    await userEvent.type(screen.getByLabelText('Model'), 'Catalyst 2960-X')
    await userEvent.click(screen.getByRole('button', { name: /Register it/ }))

    await waitFor(() => expect(assetsApi.saveProductCatalogEntry).toHaveBeenCalledWith({
      modelIdentifier: 'WS-C2960X-24TS-L',
      manufacturer: 'Cisco',
      model: 'Catalyst 2960-X',
    }))
  })

  it('does not let a second code overwrite the model already named', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified({
      identifiers: [
        { scanned: 'WS-C2960X-24TS-L', value: 'WS-C2960X-24TS-L', kind: 'Unknown' },
        { scanned: 'SOMETHING-ELSE', value: 'SOMETHING-ELSE', kind: 'Unknown' },
      ],
    }))

    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: /Scan the model code/ }))
    act(() => emitCode('WS-C2960X-24TS-L'))
    await userEvent.click(await screen.findByRole('button', { name: 'Use it' }))
    await waitFor(() => expect(screen.getByRole('button', { name: /Scan another code/ })).toBeInTheDocument())

    await userEvent.click(screen.getByRole('button', { name: /Scan another code/ }))
    act(() => emitCode('SOMETHING-ELSE'))
    await userEvent.click(await screen.findByRole('button', { name: 'Use it' }))

    // Exactly one row is the model code, and it is the first one scanned.
    const list = await screen.findByRole('list', { name: 'Scanned identifiers' })
    await waitFor(() => expect(within(list).getAllByText('Model code')).toHaveLength(1))
    const rows = within(list).getAllByRole('listitem')
    expect(within(rows[0]).getByText('Model code')).toBeInTheDocument()
  })

  it('keeps the serial on its own field rather than the main scan', async () => {
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'Scan serial number' }))
    act(() => emitCode('DNI152602HL'))
    await userEvent.click(await screen.findByRole('button', { name: 'Use it' }))

    expect(await screen.findByLabelText('Serial number')).toHaveValue('DNI152602HL')
    expect(assetsApi.identifyDevice).toHaveBeenLastCalledWith(['S/N: DNI152602HL'])
  })

  /**
   * Reported from the field: the model code was landing in the serial field. The "fill the serial
   * from the only unclassified scan" rule claimed it, and could not see it was already spoken for
   * because the assignment had not re-rendered when the response arrived.
   */
  it('never puts the model code in the serial field', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(identified({
      identifiers: [{ scanned: 'WS-C2960X-24TS-L', value: 'WS-C2960X-24TS-L', kind: 'Unknown' }],
    }))

    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: /Scan the model code/ }))
    act(() => emitCode('WS-C2960X-24TS-L'))
    await userEvent.click(await screen.findByRole('button', { name: 'Use it' }))

    await waitFor(() => expect(screen.getByLabelText('Model code')).toHaveValue('WS-C2960X-24TS-L'))
    expect(screen.getByLabelText('Serial number')).toHaveValue('')
  })

  it('shows the scanned model code in its own field', async () => {
    vi.mocked(assetsApi.identifyDevice).mockResolvedValue(recognised)

    renderPage('/field/receive?code=1S12RQ000KUSMZ00H8S2')

    // The combined Lenovo label carries both, and each lands in the field it belongs to.
    await waitFor(() => expect(screen.getByLabelText('Model code')).toHaveValue('12RQ000KUS'))
    expect(screen.getByLabelText('Serial number')).toHaveValue('MZ00H8S2')
  })

  it('lets the model code be typed when no barcode carries it', async () => {
    vi.mocked(assetsApi.createCi).mockResolvedValue({ id: 'ci-new', name: 'Switch' } as Ci)
    vi.mocked(assetsApi.saveProductCatalogEntry).mockResolvedValue({} as never)

    renderPage()
    await userEvent.type(await screen.findByLabelText('Model code'), 'WS-C2960X-24TS-L')
    await userEvent.type(screen.getByLabelText('Device name'), 'Access switch')
    await userEvent.type(screen.getByLabelText('Manufacturer'), 'Cisco')
    await userEvent.type(screen.getByLabelText('Model'), 'Catalyst 2960-X')
    await userEvent.click(screen.getByRole('button', { name: /Register it/ }))

    await waitFor(() => expect(assetsApi.saveProductCatalogEntry).toHaveBeenCalledWith({
      modelIdentifier: 'WS-C2960X-24TS-L',
      manufacturer: 'Cisco',
      model: 'Catalyst 2960-X',
    }))
  })

  /** A read aimed at the field is a correction and replaces what is there. */
  it('replaces the model code when the scan came from its own field', async () => {
    renderPage()

    await userEvent.type(await screen.findByLabelText('Model code'), 'WRONG-CODE')
    await userEvent.click(screen.getByRole('button', { name: 'Scan model code' }))
    act(() => emitCode('WS-C2960X-24TS-L'))
    await userEvent.click(await screen.findByRole('button', { name: 'Use it' }))

    await waitFor(() => expect(screen.getByLabelText('Model code')).toHaveValue('WS-C2960X-24TS-L'))
  })

  /**
   * Reported from the field: cropping to the guide narrowed the target but still fired the instant
   * anything resolved, which on a crowded label is before the technician has finished aiming.
   */
  it('waits for a shutter press rather than reading whatever comes into view', async () => {
    renderPage()

    await screen.findByLabelText('Device name')
    expect(scanManual).toBe(true)
  })

  it('reads only when the shutter is pressed', async () => {
    captureResult = 'WS-C2960X-24TS-L'

    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: /Scan the model code/ }))

    // Camera is open and nothing has been read yet.
    expect(assetsApi.identifyDevice).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('button', { name: /Read barcode/ }))
    await userEvent.click(await screen.findByRole('button', { name: 'Use it' }))

    await waitFor(() => expect(screen.getByLabelText('Model code')).toHaveValue('WS-C2960X-24TS-L'))
  })

  /** A press that finds nothing must say so, not look like a dead button. */
  it('says when a press read nothing', async () => {
    captureResult = null

    renderPage()
    await userEvent.click(await screen.findByRole('button', { name: /Scan the model code/ }))
    await userEvent.click(screen.getByRole('button', { name: /Read barcode/ }))

    expect(await screen.findByText(/Nothing readable in the frame/)).toBeInTheDocument()
  })

  /**
   * Reported from the field: a misread landed straight in the field and stayed there, so backing out
   * of the scan left the wrong code behind. Nothing is written until it is confirmed.
   */
  it('writes nothing until the read is confirmed', async () => {
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: /Scan the model code/ }))
    act(() => emitCode('WRONG-BARCODE'))

    // Held and shown, not applied. The form is behind the viewfinder, so what proves nothing was
    // written is that closing the camera leaves the field empty.
    expect(await screen.findByText('WRONG-BARCODE')).toBeInTheDocument()
    expect(assetsApi.identifyDevice).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('button', { name: 'Close camera' }))
    expect(await screen.findByLabelText('Model code')).toHaveValue('')
  })

  it('discards a misread and takes the next one instead', async () => {
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: /Scan the model code/ }))
    act(() => emitCode('WRONG-BARCODE'))
    await userEvent.click(await screen.findByRole('button', { name: 'Try again' }))

    // Back to the shutter, camera still open, so the next read is one press away.
    expect(screen.getByRole('button', { name: /Read barcode/ })).toBeInTheDocument()

    act(() => emitCode('WS-C2960X-24TS-L'))
    await userEvent.click(await screen.findByRole('button', { name: 'Use it' }))

    await waitFor(() => expect(screen.getByLabelText('Model code')).toHaveValue('WS-C2960X-24TS-L'))
  })

  it('leaves the form untouched when the camera is closed on a held read', async () => {
    renderPage()

    await userEvent.type(await screen.findByLabelText('Model code'), 'TYPED-CODE')
    await userEvent.click(screen.getByRole('button', { name: 'Scan model code' }))
    act(() => emitCode('WRONG-BARCODE'))
    // Deliberately not confirmed: closing on a held read is the case under test.
    await userEvent.click(await screen.findByRole('button', { name: 'Close camera' }))

    // Cancelling is not a half-applied scan: what was there before is what is there after.
    expect(screen.getByLabelText('Model code')).toHaveValue('TYPED-CODE')
  })

  /**
   * Manufacturer and Model hold what a person calls the thing; no barcode carries that. Offering a
   * camera there promised something impossible, so the buttons are gone — the model *code* has its
   * own field, and that is what is actually printed.
   */
  it('offers no camera on the attribute fields, which hold names rather than codes', async () => {
    renderPage('/field/receive?code=5CD1234ABC&checked=1')

    await screen.findByLabelText('Manufacturer')
    expect(screen.queryByRole('button', { name: 'Scan Manufacturer' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Scan Model' })).not.toBeInTheDocument()
    // The two that are printed keep theirs.
    expect(screen.getByRole('button', { name: 'Scan model code' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Scan serial number' })).toBeInTheDocument()
  })

  /**
   * The opening line sits above the viewfinder and is always on screen, so it kept telling the
   * technician to scan a model code while they were aiming at a serial.
   */
  it('says which code the camera is reading, not always the model', async () => {
    renderPage()

    await userEvent.click(await screen.findByRole('button', { name: 'Scan serial number' }))

    expect(screen.getByText('Reading the serial number.')).toBeInTheDocument()
    expect(screen.queryByText(/Start with the model or product code/)).not.toBeInTheDocument()
  })




  /**
   * These were computed and validated but never rendered after the rewrite onto the identification
   * API, so a required custom field left Register permanently disabled with nothing on screen to
   * explain it — the same shape of silent dead end as the missing attributes before them.
   */
  it('renders the type\'s custom fields and demands the required ones', async () => {
    vi.mocked(assetsApi.listTypeSchemas).mockResolvedValue([{
      ...schemas[0],
      customFields: [
        { id: 'cf-1', ciType: 'Hardware', key: 'costCentre', label: 'Cost centre', type: 'Text', isRequired: true, options: [], sortOrder: 0 },
        { id: 'cf-2', ciType: 'Hardware', key: 'notes', label: 'Notes', type: 'Text', isRequired: false, options: [], sortOrder: 1 },
      ],
    }])

    renderPage('/field/receive?code=5CD1234ABC&checked=1')

    await userEvent.type(await screen.findByLabelText('Device name'), 'Reception laptop')
    await userEvent.type(screen.getByLabelText('Manufacturer'), 'Dell')
    await userEvent.type(screen.getByLabelText('Model'), 'Latitude 5450')
    expect(screen.getByLabelText('Notes (optional)')).toBeInTheDocument()

    // Everything else is filled; the required custom field is the only thing still holding it.
    expect(screen.getByRole('button', { name: /Register it/ })).toBeDisabled()

    await userEvent.type(screen.getByLabelText('Cost centre'), 'CC-42')
    expect(screen.getByRole('button', { name: /Register it/ })).toBeEnabled()
  })

})
