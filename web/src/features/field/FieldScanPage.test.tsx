import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { assetsApi, type Ci } from '../../api/assets'
import { ApiError } from '../../api/client'
import { FieldScanPage } from './FieldScanPage'

vi.mock('../../api/assets', async (original) => {
  const actual = await original<typeof import('../../api/assets')>()
  return { ...actual, assetsApi: { ...actual.assetsApi, lookupCi: vi.fn() } }
})

const laptop = { id: 'ci-1', name: 'Reception laptop' } as Ci

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } })
  return render(<MemoryRouter initialEntries={['/field/scan']}>
    <QueryClientProvider client={client}>
      <Routes>
        <Route path="/field/scan" element={<FieldScanPage />} />
        <Route path="/field/ci/:id" element={<h1>Field asset page</h1>} />
      </Routes>
    </QueryClientProvider>
  </MemoryRouter>)
}

/** jsdom has no camera; each test states what the device offers. */
function withCamera(stream: MediaStream | Error | null) {
  if (stream === null) {
    Object.defineProperty(navigator, 'mediaDevices', { value: undefined, configurable: true })
    return
  }
  Object.defineProperty(navigator, 'mediaDevices', {
    value: {
      getUserMedia: vi.fn().mockImplementation(() => stream instanceof Error ? Promise.reject(stream) : Promise.resolve(stream)),
    },
    configurable: true,
  })
}

describe('FieldScanPage', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    withCamera(null)
  })

  it('opens the field screen for a code it resolves, not the desktop one', async () => {
    vi.mocked(assetsApi.lookupCi).mockResolvedValue(laptop)

    renderPage()

    await userEvent.type(screen.getByLabelText('Asset tag or serial'), 'LT-00421')
    await userEvent.click(screen.getByRole('button', { name: /Find asset/ }))

    expect(await screen.findByRole('heading', { name: 'Field asset page' })).toBeInTheDocument()
    expect(assetsApi.lookupCi).toHaveBeenCalledWith('LT-00421')
  })

  it('tells the technician the code matched nothing rather than failing silently', async () => {
    vi.mocked(assetsApi.lookupCi).mockRejectedValue(new ApiError(404, 'Not found'))

    renderPage()

    await userEvent.type(screen.getByLabelText('Asset tag or serial'), 'LT-99999')
    await userEvent.click(screen.getByRole('button', { name: /Find asset/ }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No asset carries that code.')
  })

  it('says the camera is refused rather than failing silently', async () => {
    const denied = new Error('Permission denied')
    denied.name = 'NotAllowedError'
    withCamera(denied)

    renderPage()
    await userEvent.click(screen.getByRole('button', { name: /Scan with camera/ }))

    expect(await screen.findByRole('alert')).toHaveTextContent('not allowed to use the camera')
  })

  it('offers the typed field when the device has no camera at all', async () => {
    withCamera(null)

    renderPage()
    await userEvent.click(screen.getByRole('button', { name: /Scan with camera/ }))

    expect(await screen.findByRole('alert')).toHaveTextContent('No camera is available here')
    expect(screen.getByLabelText('Asset tag or serial')).toBeInTheDocument()
  })

  /**
   * Regression: onError first restarted the camera unconditionally, so mistyping a tag on a device
   * with no camera surfaced "No camera is available" on top of the real "no such code" answer.
   */
  it('does not reach for the camera when it was the typed field that failed', async () => {
    vi.mocked(assetsApi.lookupCi).mockRejectedValue(new ApiError(404, 'Not found'))

    renderPage()
    await userEvent.type(screen.getByLabelText('Asset tag or serial'), 'LT-99999')
    await userEvent.click(screen.getByRole('button', { name: /Find asset/ }))

    await waitFor(() => expect(screen.getByText('No asset carries that code.')).toBeInTheDocument())
    expect(screen.queryByText(/No camera is available here/)).not.toBeInTheDocument()
  })
})
