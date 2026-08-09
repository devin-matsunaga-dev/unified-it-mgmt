import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type CiImportColumns, type CiImportReport } from '../../api/assets'
import { ApiError } from '../../api/client'
import { CiImportWizard } from './CiImportWizard'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return {
    ...actual,
    assetsApi: {
      ...actual.assetsApi,
      listCis: vi.fn(),
      inspectImport: vi.fn(),
      previewImport: vi.fn(),
      commitImport: vi.fn(),
    },
  }
})

const columns: CiImportColumns = {
  fileName: 'laptops.csv',
  headers: ['Name', 'Asset tag', 'Serial', 'Make', 'Model'],
  sampleRows: [['laptop-1', 'AT-1', 'SN-1', 'Dell', 'Latitude 5550']],
  rowCount: 2,
  targets: [
    { key: 'name', label: 'Name', isRequired: true, kind: 'Core' },
    { key: 'assetTag', label: 'Asset tag', isRequired: false, kind: 'Core' },
    { key: 'serialNumber', label: 'Serial number', isRequired: false, kind: 'Core' },
    { key: 'description', label: 'Description', isRequired: false, kind: 'Core' },
    { key: 'attributes.manufacturer', label: 'Manufacturer', isRequired: true, kind: 'Attribute' },
    { key: 'attributes.model', label: 'Model', isRequired: true, kind: 'Attribute' },
  ],
  suggestedMapping: {
    name: 'Name',
    assetTag: 'Asset tag',
    serialNumber: 'Serial',
    'attributes.model': 'Model',
  },
}

const dryRun: CiImportReport = {
  isDryRun: true,
  totalRows: 2,
  created: 1,
  updated: 0,
  skipped: 0,
  failed: 1,
  rows: [
    { lineNumber: 2, action: 'Create', name: 'laptop-1', assetTag: 'AT-1', serialNumber: 'SN-1', matchedCiId: null, errors: [] },
    { lineNumber: 3, action: 'Error', name: 'laptop-2', assetTag: 'AT-2', serialNumber: 'SN-2', matchedCiId: null, errors: ['Manufacturer is required for a Hardware CI.'] },
  ],
}

/** What the server offers for a mixed file: the type column plus the union of every type's columns. */
const mixedColumns: CiImportColumns = {
  fileName: 'estate.csv',
  headers: ['Name', 'Asset tag', 'Serial', 'Make', 'Hostname', 'Hypervisor'],
  sampleRows: [['laptop-1', 'AT-1', 'SN-1', 'Dell', '', '']],
  rowCount: 2,
  targets: [
    { key: 'name', label: 'Name', isRequired: true, kind: 'Core' },
    { key: 'assetTag', label: 'Asset tag', isRequired: false, kind: 'Core' },
    { key: 'type', label: 'CI type', isRequired: false, kind: 'Core' },
    {
      key: 'attributes.hostname', label: 'Hostname', isRequired: false, kind: 'Attribute',
      types: [{ type: 'Server', isRequired: true }, { type: 'Virtual', isRequired: true }],
    },
    {
      key: 'attributes.serviceTier', label: 'Service tier', isRequired: false, kind: 'Attribute',
      types: [{ type: 'Logical', isRequired: false }],
    },
  ],
  suggestedMapping: { name: 'Name', assetTag: 'Asset tag' },
}

const guessedDryRun: CiImportReport = {
  isDryRun: true,
  totalRows: 2,
  created: 2,
  updated: 0,
  skipped: 0,
  failed: 0,
  rows: [
    { lineNumber: 2, action: 'Create', name: 'laptop-1', assetTag: 'AT-1', serialNumber: 'SN-1', matchedCiId: null, errors: [], type: 'Hardware', typeSource: 'Inferred' },
    { lineNumber: 3, action: 'Create', name: 'vm-01', assetTag: 'AT-2', serialNumber: 'SN-2', matchedCiId: null, errors: [], type: 'Virtual', typeSource: 'Inferred' },
  ],
}

function renderWizard() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><CiImportWizard /></QueryClientProvider></MemoryRouter>)
}

function csvFile() {
  return new File(['Name,Asset tag\nlaptop-1,AT-1\n'], 'laptops.csv', { type: 'text/csv' })
}

async function reachTheMappingStep() {
  renderWizard()
  await userEvent.upload(screen.getByLabelText('File'), csvFile())
  await userEvent.click(screen.getByRole('button', { name: 'Read columns' }))
  expect(await screen.findByText(/Map the columns of laptops.csv/)).toBeInTheDocument()
}

describe('CiImportWizard', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.mocked(assetsApi.listCis).mockResolvedValue({ items: [], total: 0, page: 1, pageSize: 1 })
    vi.mocked(assetsApi.inspectImport).mockResolvedValue(columns)
    vi.mocked(assetsApi.previewImport).mockResolvedValue(dryRun)
    vi.mocked(assetsApi.commitImport).mockResolvedValue({ ...dryRun, isDryRun: false })
  })

  it('cannot read columns before a file is chosen', async () => {
    renderWizard()

    expect(screen.getByRole('button', { name: 'Read columns' })).toBeDisabled()
    expect(assetsApi.inspectImport).not.toHaveBeenCalled()
  })

  it('sends the chosen type with the file and prefills the suggested mapping', async () => {
    renderWizard()

    await userEvent.selectOptions(screen.getByLabelText('CI type'), 'Server')
    await userEvent.upload(screen.getByLabelText('File'), csvFile())
    await userEvent.click(screen.getByRole('button', { name: 'Read columns' }))

    await waitFor(() => expect(assetsApi.inspectImport).toHaveBeenCalledWith(expect.any(File), 'Server'))
    expect(await screen.findByLabelText('Asset tag')).toHaveDisplayValue('Asset tag')
    // The server had no guess for this one, so it stays unmapped rather than being invented.
    expect(screen.getByLabelText('Manufacturer')).toHaveDisplayValue('Not imported')
  })

  it('previews with the mapping the operator corrected, and writes nothing yet', async () => {
    await reachTheMappingStep()

    await userEvent.selectOptions(screen.getByLabelText('Manufacturer'), 'Make')
    await userEvent.click(screen.getByRole('button', { name: 'Run the dry run' }))

    await waitFor(() => expect(assetsApi.previewImport).toHaveBeenCalledWith(expect.any(File), {
      type: 'Hardware',
      columns: { ...columns.suggestedMapping, 'attributes.manufacturer': 'Make' },
    }))
    expect(await screen.findByText('Nothing has been written yet')).toBeInTheDocument()
    expect(assetsApi.commitImport).not.toHaveBeenCalled()
  })

  it('shows the failing row with its line number and still offers to import the rest', async () => {
    await reachTheMappingStep()
    await userEvent.click(screen.getByRole('button', { name: 'Run the dry run' }))

    const failing = within(await screen.findByRole('row', { name: /Manufacturer is required/ }))
    expect(failing.getByText('3')).toBeInTheDocument()
    expect(failing.getByText('Error')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Import 1 rows' })).toBeEnabled()
  })

  it('commits only after the dry run and reports what happened', async () => {
    await reachTheMappingStep()
    await userEvent.click(screen.getByRole('button', { name: 'Run the dry run' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Import 1 rows' }))

    await waitFor(() => expect(assetsApi.commitImport).toHaveBeenCalledTimes(1))
    expect(await screen.findByText(/1 created, 0 updated, 0 skipped, 1 failed/)).toBeInTheDocument()
  })

  it('offers nothing to import when every row would be skipped', async () => {
    vi.mocked(assetsApi.previewImport).mockResolvedValue({
      ...dryRun, created: 0, failed: 0, skipped: 2,
      rows: dryRun.rows.map((row) => ({ ...row, action: 'Skip' as const, errors: [] })),
    })
    await reachTheMappingStep()

    await userEvent.click(screen.getByRole('button', { name: 'Run the dry run' }))

    expect(await screen.findByText('No row would change anything, so there is nothing to import.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Import 0 rows' })).toBeDisabled()
  })

  it('surfaces the mapping errors the server returned', async () => {
    vi.mocked(assetsApi.previewImport).mockRejectedValue(new ApiError(400, 'Validation failed', {
      'mapping.assetTag': ['Map a column to Asset tag or Serial number so rows can be matched to existing CIs.'],
    }))
    await reachTheMappingStep()

    await userEvent.click(screen.getByRole('button', { name: 'Run the dry run' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Map a column to Asset tag or Serial number')
  })

  it('asks the server for every type\'s columns when the file is mixed', async () => {
    vi.mocked(assetsApi.inspectImport).mockResolvedValue(mixedColumns)
    renderWizard()

    await userEvent.selectOptions(screen.getByLabelText('CI type'), 'Mixed')
    await userEvent.upload(screen.getByLabelText('File'), csvFile())
    await userEvent.click(screen.getByRole('button', { name: 'Read columns' }))

    await waitFor(() => expect(assetsApi.inspectImport).toHaveBeenCalledWith(expect.any(File), 'Mixed'))
    expect(await screen.findByLabelText('CI type', { selector: 'select.input' })).toBeInTheDocument()
    // The "already registered" count is meaningless across six types, so it is not asked for.
    expect(assetsApi.listCis).not.toHaveBeenCalledWith(expect.objectContaining({ type: 'Mixed' }))
  })

  it('says which types need a column several of them share', async () => {
    vi.mocked(assetsApi.inspectImport).mockResolvedValue(mixedColumns)
    renderWizard()
    await userEvent.selectOptions(screen.getByLabelText('CI type'), 'Mixed')
    await userEvent.upload(screen.getByLabelText('File'), csvFile())
    await userEvent.click(screen.getByRole('button', { name: 'Read columns' }))

    expect(await screen.findByText('Required for Server, Virtual machine')).toBeInTheDocument()
    expect(screen.getByText('Optional for Logical')).toBeInTheDocument()
  })

  it('marks a guessed type in the dry run and warns before it is committed', async () => {
    vi.mocked(assetsApi.inspectImport).mockResolvedValue(mixedColumns)
    vi.mocked(assetsApi.previewImport).mockResolvedValue(guessedDryRun)
    renderWizard()
    await userEvent.selectOptions(screen.getByLabelText('CI type'), 'Mixed')
    await userEvent.upload(screen.getByLabelText('File'), csvFile())
    await userEvent.click(screen.getByRole('button', { name: 'Read columns' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Run the dry run' }))

    const row = within(await screen.findByRole('row', { name: /vm-01/ }))
    expect(row.getByText('Virtual machine')).toBeInTheDocument()
    expect(row.getByText('guessed')).toBeInTheDocument()
    expect(screen.getByText(/2 rows have a guessed type/)).toBeInTheDocument()
  })

  it('tells the server the guessed types were seen when it commits', async () => {
    vi.mocked(assetsApi.inspectImport).mockResolvedValue(mixedColumns)
    vi.mocked(assetsApi.previewImport).mockResolvedValue(guessedDryRun)
    vi.mocked(assetsApi.commitImport).mockResolvedValue({ ...guessedDryRun, isDryRun: false })
    renderWizard()
    await userEvent.selectOptions(screen.getByLabelText('CI type'), 'Mixed')
    await userEvent.upload(screen.getByLabelText('File'), csvFile())
    await userEvent.click(screen.getByRole('button', { name: 'Read columns' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Run the dry run' }))
    await userEvent.click(await screen.findByRole('button', { name: 'Import 2 rows' }))

    await waitFor(() => expect(assetsApi.commitImport).toHaveBeenCalledWith(expect.any(File), {
      type: 'Mixed',
      columns: mixedColumns.suggestedMapping,
      acceptInferredTypes: true,
    }))
  })

  it('reports a file the server could not read', async () => {
    vi.mocked(assetsApi.inspectImport).mockRejectedValue(new ApiError(400, 'Upload a .csv or .xlsx file.'))
    renderWizard()

    await userEvent.upload(screen.getByLabelText('File'), csvFile())
    await userEvent.click(screen.getByRole('button', { name: 'Read columns' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Upload a .csv or .xlsx file.')
  })
})
