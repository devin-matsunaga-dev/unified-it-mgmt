import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { vi } from 'vitest'
import { ApiError } from '../../api/client'
import { softwareApi, type SoftwareImportReport } from '../../api/software'
import { SoftwareImportPage } from './SoftwareImportPage'

vi.mock('../../api/software', async (original) => {
  const actual = await original<typeof import('../../api/software')>()
  return { ...actual, softwareApi: { previewImport: vi.fn(), commitImport: vi.fn() } }
})

vi.mock('sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }))

const dryRun: SoftwareImportReport = {
  isDryRun: true,
  fileName: 'inventory.csv',
  totalRows: 3,
  created: 2,
  updated: 0,
  failed: 1,
  machinesMatched: 2,
  normalised: 2,
  unrecognised: 0,
  rows: [
    { lineNumber: 2, action: 'Create', machine: 'LT-0001', softwareName: 'Google Chrome', version: '121.0', ciId: 'ci-1', ciName: 'Laptop LT-0001', productId: 'p-1', productName: 'Chrome', errors: [] },
    { lineNumber: 3, action: 'Create', machine: 'LT-0002', softwareName: 'Google Chrome', version: '121.0', ciId: 'ci-2', ciName: 'Laptop LT-0002', productId: 'p-1', productName: 'Chrome', errors: [] },
    { lineNumber: 4, action: 'Error', machine: 'LT-9999', softwareName: 'Google Chrome', version: '121.0', ciId: null, ciName: null, productId: null, productName: null, errors: ["No CI matches asset tag 'LT-9999'."] },
  ],
  unrecognisedNames: [],
}

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter><QueryClientProvider client={client}><SoftwareImportPage /></QueryClientProvider></MemoryRouter>)
}

function chooseFile(contents = 'asset tag,software\nLT-0001,Google Chrome\n') {
  return new File([contents], 'inventory.csv', { type: 'text/csv' })
}

describe('SoftwareImportPage', () => {
  beforeEach(() => vi.clearAllMocks())

  it('previews before anything is written, and names the row that could not be placed', async () => {
    vi.mocked(softwareApi.previewImport).mockResolvedValue(dryRun)

    renderPage()
    await userEvent.upload(screen.getByLabelText('File'), chooseFile())
    await userEvent.click(screen.getByRole('button', { name: /Preview/ }))

    expect(await screen.findByText('What this import will do')).toBeInTheDocument()
    expect(screen.getByText("No CI matches asset tag 'LT-9999'.")).toBeInTheDocument()
    expect(softwareApi.commitImport).not.toHaveBeenCalled()
  })

  it('commits the same file it previewed', async () => {
    vi.mocked(softwareApi.previewImport).mockResolvedValue(dryRun)
    vi.mocked(softwareApi.commitImport).mockResolvedValue({ ...dryRun, isDryRun: false })

    renderPage()
    const file = chooseFile()
    await userEvent.upload(screen.getByLabelText('File'), file)
    await userEvent.click(screen.getByRole('button', { name: /Preview/ }))
    await screen.findByText('What this import will do')
    await userEvent.click(screen.getByRole('button', { name: /Import 2 rows/ }))

    await waitFor(() => expect(softwareApi.commitImport).toHaveBeenCalledWith(file))
    expect(await screen.findByText('What this import did')).toBeInTheDocument()
  })

  /** The failure path: a file with the wrong columns is refused with the sentence naming what it reads. */
  it('surfaces a refused file rather than an empty report', async () => {
    vi.mocked(softwareApi.previewImport).mockRejectedValue(
      new ApiError(400, "The file has no column naming the software. An inventory file needs a machine column…"))

    renderPage()
    await userEvent.upload(screen.getByLabelText('File'), chooseFile('machine\nLT-0001\n'))
    await userEvent.click(screen.getByRole('button', { name: /Preview/ }))

    expect(await screen.findByRole('alert')).toHaveTextContent('no column naming the software')
    expect(screen.getByText('Nothing previewed yet')).toBeInTheDocument()
  })
})
