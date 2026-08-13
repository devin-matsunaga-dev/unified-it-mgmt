import '@testing-library/jest-dom/vitest'
import { vi } from 'vitest'

Object.defineProperty(window, 'matchMedia', { writable: true, value: vi.fn().mockImplementation((query: string) => ({ matches: false, media: query, onchange: null, addListener: vi.fn(), removeListener: vi.fn(), addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn() })) })

/**
 * React Flow measures its canvas before it will render a node, and jsdom implements none of the APIs
 * it measures with. These stubs are the ones the library's own testing guide names; without them a
 * topology test renders an empty canvas and looks like a component bug.
 */
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}

globalThis.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver
globalThis.DOMMatrixReadOnly ??= class {
  m22 = 1
  constructor(_transform?: string) {}
} as unknown as typeof DOMMatrixReadOnly

Object.defineProperties(globalThis.HTMLElement.prototype, {
  offsetHeight: { get() { return Number.parseFloat(this.style.height) || 1 } },
  offsetWidth: { get() { return Number.parseFloat(this.style.width) || 1 } },
})

Object.assign(globalThis.SVGElement.prototype, {
  getBBox: () => ({ x: 0, y: 0, width: 0, height: 0 }),
})
