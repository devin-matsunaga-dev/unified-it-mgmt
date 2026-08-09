import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { vi } from 'vitest'
import { assetsApi, type Ci } from '../../api/assets'
import { ApiError } from '../../api/client'
import { CiLabelDialog } from './CiLabelDialog'
import { openFile, saveFile } from './labelFiles'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, getCiLabel: vi.fn(), getCiLabelSheet: vi.fn() } }
})

// jsdom has no object URLs and no second tab, so the two ways out of a PDF are stubbed and asserted on.
vi.mock('./labelFiles', () => ({ saveFile: vi.fn(), openFile: vi.fn(() => true) }))

const toasts = vi.hoisted(() => ({ error: vi.fn(), success: vi.fn() }))
vi.mock('sonner', () => ({ toast: toasts }))

const laptop: Ci = {
  id: 'ci-1', type: 'Hardware', name: 'Reception laptop', assetTag: 'LT-00421', serialNumber: '5CD1234ABC',
  description: null, isActive: true, lifecycleState: 'Deployed',
  ownership: { ownerUserId: null, ownerName: null, departmentId: null, departmentName: null, siteId: null, siteName: null, assignedAt: null },
  coverage: { contractId: null, contractName: null, contractNumber: null, vendorName: null, contractEndDate: null, purchaseDate: null, warrantyExpiresAt: null, warrantyStatus: null, warrantyDaysRemaining: null },
  attributes: {}, customFields: [], createdAt: '2026-08-08T00:00:00Z', updatedAt: '2026-08-08T00:00:00Z',
}
const server: Ci = { ...laptop, id: 'ci-2', name: 'app-01', type: 'Server', assetTag: 'AT-0001' }

const file = { blob: new Blob(['%PDF'], { type: 'application/pdf' }), fileName: 'asset-label-lt-00421.pdf' }

function renderDialog(selection: Ci[]) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<QueryClientProvider client={client}><CiLabelDialog selection={selection} onClose={vi.fn()} /></QueryClientProvider>)
}

describe('CiLabelDialog', () => {
  beforeEach(() => vi.clearAllMocks())

  it('renders nothing when nothing is selected', () => {
    const { container } = renderDialog([])

    expect(container).toBeEmptyDOMElement()
  })

  it('prints one label for a single CI and opens it for printing', async () => {
    vi.mocked(assetsApi.getCiLabel).mockResolvedValue(file)
    renderDialog([laptop])

    expect(screen.getByRole('heading', { name: 'Print label' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Open to print/ }))

    await waitFor(() => expect(assetsApi.getCiLabel).toHaveBeenCalledWith('ci-1', 'Standard'))
    expect(openFile).toHaveBeenCalledWith(file)
    expect(saveFile).not.toHaveBeenCalled()
  })

  it('sends the chosen size', async () => {
    vi.mocked(assetsApi.getCiLabel).mockResolvedValue(file)
    renderDialog([laptop])

    await userEvent.click(screen.getByRole('radio', { name: /Small/ }))
    await userEvent.click(screen.getByRole('button', { name: /Open to print/ }))

    await waitFor(() => expect(assetsApi.getCiLabel).toHaveBeenCalledWith('ci-1', 'Small'))
  })

  it('prints a sheet for a selection, in the order it was selected', async () => {
    vi.mocked(assetsApi.getCiLabelSheet).mockResolvedValue({ ...file, fileName: 'asset-labels-2.pdf' })
    renderDialog([laptop, server])

    expect(screen.getByRole('heading', { name: 'Print 2 labels' })).toBeInTheDocument()
    await userEvent.click(screen.getByRole('button', { name: /Download PDF/ }))

    await waitFor(() => expect(assetsApi.getCiLabelSheet).toHaveBeenCalledWith(['ci-1', 'ci-2'], 'Standard'))
    expect(saveFile).toHaveBeenCalled()
    expect(assetsApi.getCiLabel).not.toHaveBeenCalled()
  })

  it('reports a failed generation instead of opening an empty tab', async () => {
    vi.mocked(assetsApi.getCiLabelSheet).mockRejectedValue(new ApiError(404, "CI 'ci-2' does not exist."))
    renderDialog([laptop, server])

    await userEvent.click(screen.getByRole('button', { name: /Open to print/ }))

    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith("CI 'ci-2' does not exist."))
    expect(openFile).not.toHaveBeenCalled()
  })

  it('tells the operator to download when the browser blocks the new tab', async () => {
    vi.mocked(assetsApi.getCiLabel).mockResolvedValue(file)
    vi.mocked(openFile).mockReturnValue(false)
    renderDialog([laptop])

    await userEvent.click(screen.getByRole('button', { name: /Open to print/ }))

    await waitFor(() => expect(toasts.error).toHaveBeenCalledWith(expect.stringContaining('Download')))
  })
})
