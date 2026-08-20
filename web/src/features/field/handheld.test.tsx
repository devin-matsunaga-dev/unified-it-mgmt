import { render, screen } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import { vi } from 'vitest'
import { HandheldRedirect, fieldPathFor } from './HandheldRedirect'

/** jsdom has no matchMedia; every test states the viewport it is describing. */
function setViewport({ handheld }: { handheld: boolean }) {
  vi.stubGlobal('matchMedia', vi.fn().mockReturnValue({
    matches: handheld,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
  }))
}

function renderAt(path: string) {
  return render(<MemoryRouter initialEntries={[path]}>
    <Routes>
      <Route element={<HandheldRedirect><h1>Agent shell</h1></HandheldRedirect>}>
        <Route path="/assets/:id" element={null} />
        <Route path="/admin/settings/sla" element={null} />
        <Route path="/tickets" element={null} />
        <Route path="/scan" element={null} />
      </Route>
      <Route path="/field/ci/:id" element={<h1>Field asset page</h1>} />
      <Route path="/field/scan" element={<h1>Field scan page</h1>} />
    </Routes>
  </MemoryRouter>)
}

describe('fieldPathFor', () => {
  it('keeps the asset a scanned label named', () => {
    expect(fieldPathFor('/assets/ci-1')).toBe('/field/ci/ci-1')
    expect(fieldPathFor('/assets/ci-1/')).toBe('/field/ci/ci-1')
  })

  it('maps the desktop scan page to the field one', () => {
    expect(fieldPathFor('/scan')).toBe('/field/scan')
  })

  it('keeps the ticket and count a desktop route named', () => {
    expect(fieldPathFor('/tickets')).toBe('/field/tickets')
    expect(fieldPathFor('/tickets/t-1')).toBe('/field/tickets/t-1')
    expect(fieldPathFor('/audits')).toBe('/field/audits')
    expect(fieldPathFor('/audits/a-1')).toBe('/field/audits/a-1')
  })

  it('falls back to the scan screen for anything with no field equivalent', () => {
    expect(fieldPathFor('/admin/settings/sla')).toBe('/field/scan')
    expect(fieldPathFor('/')).toBe('/field/scan')
  })
})

describe('HandheldRedirect', () => {
  afterEach(() => vi.unstubAllGlobals())

  it('sends a phone to the field screen for the asset it actually opened', () => {
    setViewport({ handheld: true })
    renderAt('/assets/ci-1')
    expect(screen.getByRole('heading', { name: 'Field asset page' })).toBeInTheDocument()
  })

  it('keeps a phone out of every other agent route, not just the asset page', () => {
    setViewport({ handheld: true })
    renderAt('/admin/settings/sla')
    expect(screen.getByRole('heading', { name: 'Field scan page' })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Agent shell' })).not.toBeInTheDocument()
  })

  it('leaves a desktop browser on the agent shell', () => {
    setViewport({ handheld: false })
    renderAt('/tickets')
    expect(screen.getByRole('heading', { name: 'Agent shell' })).toBeInTheDocument()
  })
})
