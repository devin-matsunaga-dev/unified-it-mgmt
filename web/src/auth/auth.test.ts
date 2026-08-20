import { normalizeRoles, userManager } from './auth'

describe('role authorization', () => {
  it('keeps only application roles returned by the API', () => {
    expect(normalizeRoles(['Admin', 'offline_access', 'Technician'])).toEqual(['Admin', 'Technician'])
  })

  it('returns no roles for a malformed API response', () => {
    expect(normalizeRoles('Admin')).toEqual([])
  })
})

describe('session storage', () => {
  /**
   * A scanned QR opens a new tab, and sessionStorage does not cross one. Storing the user there sent
   * a technician round the full redirect sign-in on every label they scanned.
   */
  it('keeps the signed-in user somewhere a new tab can read', () => {
    const store = (userManager.settings.userStore as unknown as { _store: Storage })._store
    expect(store).toBe(window.localStorage)
    expect(store).not.toBe(window.sessionStorage)
  })
})
